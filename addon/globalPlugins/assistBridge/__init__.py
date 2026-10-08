# AssistBridge backend global plugin.
# Copyright (C) 2026 AssistBridge contributors.
# Portions adapted from NVDA's _remoteClient package:
# Copyright (C) 2015-2026 NV Access Limited, Christopher Toth, Tyler Spivey, Babbage B.V., David Sexton and others.
# This file is covered by the GNU General Public License, version 2 or later.

"""Runs NVDA's own Remote Access sessions on behalf of the AssistBridge app.

AssistBridge owns all networking (relay servers, TLS, certificates, the user interface).
This plugin owns everything that has to happen inside NVDA, using NVDA's built-in
Remote Access classes so that behaviour and wire format match NVDA exactly:

* As the controlled computer (follower): speech, cancel/pause, tones, sounds and braille
  produced by NVDA are serialised by NVDA's own session code and handed to AssistBridge,
  which forwards them to the controlling computer. Keys, braille input and braille display
  sizes from the controlling computer are executed here.
* As the controlling computer (leader): speech, sounds and braille from the remote computer
  are rendered by this NVDA with a real voice, and keyboard/braille input is captured and
  handed to AssistBridge while the user is controlling the remote computer.

The link to AssistBridge is a newline-delimited JSON stream on a loopback TCP socket.
Messages whose type starts with ``bridge_`` are control messages between this plugin and
AssistBridge; every other message is a Remote Access protocol message.
"""

import json
import os
import socket
import threading
import time
from typing import Any

import api
import braille
import config
import core
import globalPluginHandler
import inputCore
import nvwave
import scriptHandler
import speech
import speech.extensions
import synthDriverHandler
import tones
import winUser
import wx
from keyboardHandler import KeyboardInputGesture, canModifiersPerformAction
from logHandler import log
from speech.priorities import Spri
from utils.security import post_sessionLockStateChanged

from _remoteClient import cues as _nvdaCues
from _remoteClient.connectionInfo import ConnectionMode
from _remoteClient.localMachine import LocalMachine
from _remoteClient.protocol import RemoteMessageType
from _remoteClient.serializer import JSONSerializer
from _remoteClient.session import FollowerSession, LeaderSession
from _remoteClient.transport import Transport

try:
	# NVDA 2026.3 and later: braille is a package with its extension points in braille.extensions.
	import braille.extensions as _brailleExtensions
except ImportError:
	# NVDA 2025.1 to 2026.2: the extension points are attributes of the braille module.
	_brailleExtensions = braille

PORT_ENV = "ASSISTBRIDGE_PORT"
TOKEN_ENV = "ASSISTBRIDGE_TOKEN"
SILENT_SYNTH = "assistBridgeSilent"
BRIDGE_PREFIX = "bridge_"

DEFAULT_CONFIG: dict[str, Any] = {
	# Leader: whether this NVDA also reads the local computer (a sighted user usually does not want this).
	"readLocalScreen": False,
	# Follower: whether the person at this computer also hears the speech that is being forwarded.
	"speakLocallyWhenControlled": False,
	# Follower: whether NVDA's sounds and beeps are audible on this computer.
	"localSoundsWhenControlled": False,
	# Synthesizer used whenever this NVDA speaks aloud ("auto" lets NVDA choose).
	"synth": "auto",
	"rate": None,
	"volume": None,
	"muteOnLocalControl": False,
	# Follower: pace of the silent relay synthesizer, matching the controlling computer's voice.
	"estimatedRate": 50,
	# Gestures that switch keyboard control between this computer and the remote one.
	"toggleGestures": ["kb:NVDA+alt+tab", "kb:control+alt+shift+f11"],
	"sasGestures": [],
	"pushClipboardGestures": ["kb:NVDA+control+shift+c"],
	"toggleMuteGestures": [],
}


class ClientLinkTransport(Transport):
	"""A Remote Access transport whose far end is the AssistBridge app rather than a socket.

	NVDA's sessions talk to this exactly as they would to a ``RelayTransport``.
	AssistBridge performs the real network I/O and tells us when the network is up.
	"""

	def __init__(self, link: "ClientLink", address: tuple[str, int], channel: str, connectionType: str):
		super().__init__(serializer=JSONSerializer())
		self.link = link
		self.address = address
		self.channel = channel
		self.connectionType = connectionType
		self.closed = False

	def run(self) -> None:
		"""Networking is performed by AssistBridge, so there is nothing to run here."""

	def setNetworkConnected(self, connected: bool) -> None:
		if self.closed:
			return
		if connected and not self.connected:
			self.onTransportConnected()
		elif not connected and self.connected:
			self.connected = False
			self.connectedEvent.clear()
			self.transportDisconnected.notify()

	def send(self, type: RemoteMessageType, **kwargs: Any) -> None:
		if not self.connected or self.closed:
			return
		try:
			data = self.serializer.serialize(type=type, **kwargs)
		except (TypeError, ValueError):
			log.debugWarning(f"Unable to serialise outbound {type!r}", exc_info=True)
			return
		self.link.sendRaw(data)

	def parse(self, line: bytes) -> None:
		"""Route an inbound Remote Access message to the session's handlers (mirrors TCPTransport.parse)."""
		if self.closed:
			return
		try:
			obj = self.serializer.deserialize(line)
		except ValueError:
			log.warning(f"Malformed message from AssistBridge: {line!r}")
			return
		try:
			messageType = RemoteMessageType(obj.get("type"))
		except ValueError:
			log.debug(f"Ignoring unknown Remote Access message: {obj!r}")
			return
		if messageType is RemoteMessageType.PING:
			return
		del obj["type"]
		extensionPoint = self.inboundHandlers.get(messageType)
		if not extensionPoint:
			log.debug(f"No handler for {messageType}")
			return
		wx.CallAfter(extensionPoint.notify, **obj)

	def close(self) -> None:
		if self.closed:
			return
		self.transportClosing.notify()
		self.connected = False
		self.connectedEvent.clear()
		self.closed = True


class ClientLink:
	"""Loopback connection to the AssistBridge app."""

	def __init__(self, plugin: "GlobalPlugin", port: int, token: str):
		self.plugin = plugin
		self.port = port
		self.token = token
		self.sock: socket.socket | None = None
		self._sendLock = threading.Lock()
		self._running = True
		self._thread = threading.Thread(target=self._run, name="assistBridgeLink", daemon=True)
		self._thread.start()

	def _connect(self) -> socket.socket:
		sock = socket.create_connection(("127.0.0.1", self.port), timeout=10)
		sock.settimeout(None)
		sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
		return sock

	def _run(self) -> None:
		failures = 0
		while self._running:
			try:
				self.sock = self._connect()
			except OSError:
				failures += 1
				if failures > 30:
					log.error("AssistBridge is not reachable; exiting the bundled NVDA.")
					wx.CallAfter(core.triggerNVDAExit)
					return
				time.sleep(1)
				continue
			failures = 0
			self.sendControl(
				"hello",
				token=self.token,
				nvdaVersion=_nvdaVersion(),
				pid=os.getpid(),
				synths=_synthList(),
			)
			wx.CallAfter(self.plugin.onLinkUp)
			buffer = b""
			try:
				while self._running:
					data = self.sock.recv(65536)
					if not data:
						break
					buffer += data
					while b"\n" in buffer:
						line, _sep, buffer = buffer.partition(b"\n")
						if line.strip():
							self._handleLine(line)
			except OSError:
				pass
			self._closeSocket()
			if not self._running:
				return
			# AssistBridge went away. It always owns this NVDA, so shut down rather than linger.
			log.info("Link to AssistBridge closed; exiting.")
			wx.CallAfter(self.plugin.onLinkLost)
			return

	def _handleLine(self, line: bytes) -> None:
		try:
			head = json.loads(line)
		except ValueError:
			log.warning(f"Malformed line from AssistBridge: {line[:200]!r}")
			return
		msgType = head.get("type", "")
		if isinstance(msgType, str) and msgType.startswith(BRIDGE_PREFIX):
			wx.CallAfter(self.plugin.handleControl, msgType[len(BRIDGE_PREFIX) :], head)
		else:
			self.plugin.handleRemoteLine(line)

	def sendRaw(self, data: bytes) -> None:
		sock = self.sock
		if sock is None:
			return
		try:
			with self._sendLock:
				sock.sendall(data)
		except OSError:
			log.debugWarning("Unable to write to AssistBridge link", exc_info=True)

	def sendControl(self, controlName: str, /, **kwargs: Any) -> None:
		kwargs["type"] = BRIDGE_PREFIX + controlName
		self.sendRaw(json.dumps(kwargs).encode("utf-8") + b"\n")

	def _closeSocket(self) -> None:
		sock, self.sock = self.sock, None
		if sock is not None:
			try:
				sock.close()
			except OSError:
				pass

	def close(self) -> None:
		self._running = False
		self._closeSocket()


class BridgeLocalMachine(LocalMachine):
	"""NVDA's LocalMachine, marking output that originates from the remote computer.

	The leader can then silence this computer's own screen reading while still
	rendering everything that arrives from the remote computer.
	"""

	def __init__(self, plugin: "GlobalPlugin"):
		super().__init__()
		self._plugin = plugin

	def playWave(self, fileName: str) -> None:
		if self.isMuted:
			return
		resolved = self._plugin.resolveWave(fileName)
		if resolved:
			with self._plugin.remoteOutput():
				nvwave.playWaveFile(fileName=resolved, asynchronous=True)

	def beep(self, hz: float, length: int, left: int = 50, right: int = 50) -> None:
		if self.isMuted:
			return
		with self._plugin.remoteOutput():
			tones.beep(hz, length, left, right)

	def setClipboardText(self, text: str) -> None:
		# AssistBridge handles clipboard transfers itself.
		api.copyToClip(text=text)


class _RemoteOutputContext:
	def __init__(self, plugin: "GlobalPlugin"):
		self._plugin = plugin

	def __enter__(self):
		self._plugin._remoteOutputDepth += 1

	def __exit__(self, *exc):
		self._plugin._remoteOutputDepth -= 1
		return False


def _nvdaVersion() -> str:
	try:
		import buildVersion

		return buildVersion.version
	except Exception:
		return "unknown"


def _synthList() -> list[list[str]]:
	try:
		return [[name, description] for name, description in synthDriverHandler.getSynthList()]
	except Exception:
		log.debugWarning("Unable to list synthesizers", exc_info=True)
		return []


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
	scriptCategory = "AssistBridge"

	def __init__(self):
		super().__init__()
		self.link: ClientLink | None = None
		self.cfg: dict[str, Any] = dict(DEFAULT_CONFIG)
		self.localMachine: BridgeLocalMachine | None = None
		self.session: LeaderSession | FollowerSession | None = None
		self.transport: ClientLinkTransport | None = None
		self.mode: ConnectionMode | None = None
		self.sendingKeys = False
		self.keyModifiers: set[tuple[int, bool]] = set()
		self.hostPendingModifiers: set[tuple[int, bool]] = set()
		self.hostPendingNonmodifier: tuple[int, bool] | None = None
		self._wasSendingKeysBeforeLock = False
		self._remoteOutputDepth = 0
		self._allowLocalSpeechDepth = 0
		self._savedSynth: str | None = None
		self._savedSoundVolume: int | None = None
		self._originalPlayCue = None
		portText = os.environ.get(PORT_ENV)
		token = os.environ.get(TOKEN_ENV, "")
		if not portText:
			log.info("AssistBridge backend loaded without a link port; staying idle.")
			return
		_prepareBundledConfig()
		self.localMachine = BridgeLocalMachine(self)
		self._patchCues()
		inputCore.decide_handleRawKey.register(self._processKeyInput)
		speech.extensions.filter_speechSequence.register(self._filterLocalSpeech)
		tones.decide_beep.register(self._decideLocalBeep)
		nvwave.decide_playWaveFile.register(self._decideLocalWave)
		post_sessionLockStateChanged.register(self._sessionLockStateChangeHandler)
		self._localScripts = {self.script_toggleControl, self.script_sendSAS}
		self._bindConfiguredGestures()
		self.link = ClientLink(self, int(portText), token)

	def terminate(self):
		if self.link is None:
			return
		self.stopSession(silent=True)
		inputCore.decide_handleRawKey.unregister(self._processKeyInput)
		speech.extensions.filter_speechSequence.unregister(self._filterLocalSpeech)
		tones.decide_beep.unregister(self._decideLocalBeep)
		nvwave.decide_playWaveFile.unregister(self._decideLocalWave)
		post_sessionLockStateChanged.unregister(self._sessionLockStateChangeHandler)
		self._unpatchCues()
		if self.localMachine is not None:
			self.localMachine.terminate()
			self.localMachine = None
		self.link.close()
		self.link = None
		super().terminate()

	# Link lifecycle

	def onLinkUp(self) -> None:
		self._sendState()

	def onLinkLost(self) -> None:
		self.stopSession(silent=True)
		core.triggerNVDAExit()

	# Output policy

	def remoteOutput(self) -> _RemoteOutputContext:
		return _RemoteOutputContext(self)

	def _suppressLocalOutput(self) -> bool:
		"""Whether output generated by this computer's own screen reading should be silenced."""
		return (
			self.mode is ConnectionMode.LEADER
			and not self.cfg["readLocalScreen"]
			and self._remoteOutputDepth == 0
			and self._allowLocalSpeechDepth == 0
		)

	def _filterLocalSpeech(self, speechSequence):
		# Remote speech goes straight to the speech manager and never passes through this filter.
		if self._suppressLocalOutput():
			return []
		return speechSequence

	def _decideLocalBeep(self, **kwargs) -> bool:
		return not self._suppressLocalOutput()

	def _decideLocalWave(self, **kwargs) -> bool:
		return not self._suppressLocalOutput()

	def resolveWave(self, fileName: str) -> str | None:
		"""Find a wave file sent by the remote computer.

		Remote computers send absolute paths that are only meaningful on that computer.
		NVDA's own sounds live in the same relative location in every copy, so fall back
		to our bundled copy when the exact path does not exist here.
		"""
		if fileName and os.path.isfile(fileName):
			return fileName
		import globalVars

		base = os.path.basename(fileName or "")
		if not base:
			return None
		candidate = os.path.join(globalVars.appDir, "waves", base)
		return candidate if os.path.isfile(candidate) else None

	def announce(self, text: str, *, speakLocally: bool = True) -> None:
		"""Tell the user about a state change: shown by AssistBridge, and spoken by NVDA when appropriate."""
		if self.link is not None:
			self.link.sendControl("announce", text=text)
		if not speakLocally:
			return
		if self.mode is ConnectionMode.FOLLOWER and not self.cfg["speakLocallyWhenControlled"]:
			return
		self._allowLocalSpeechDepth += 1
		try:
			speech.speakMessage(text)
		finally:
			self._allowLocalSpeechDepth -= 1

	def _patchCues(self) -> None:
		"""Route Remote Access sound cues to AssistBridge, which plays them and shows any message."""
		self._originalPlayCue = _nvdaCues._playCue

		def playCue(cueName: str) -> None:
			# Must never raise: NVDA's session handlers call this before registering their callbacks.
			try:
				if self.link is not None:
					cue = _nvdaCues.CUES.get(cueName, {})
					self.link.sendControl("cue", name=cueName, wave=cue.get("wave"), message=cue.get("message"))
			except Exception:
				log.debugWarning(f"Unable to report cue {cueName!r}", exc_info=True)

		_nvdaCues._playCue = playCue

	def _unpatchCues(self) -> None:
		if self._originalPlayCue is not None:
			_nvdaCues._playCue = self._originalPlayCue
			self._originalPlayCue = None

	def _applyAudioPolicy(self) -> None:
		"""Choose the synthesizer and sound volume for the current role."""
		try:
			if self.mode is ConnectionMode.FOLLOWER and not self.cfg["speakLocallyWhenControlled"]:
				desired = SILENT_SYNTH
			else:
				desired = self.cfg.get("synth") or "auto"
			current = synthDriverHandler.getSynth()
			if desired == "auto":
				if current is None or current.name == SILENT_SYNTH:
					synthDriverHandler.setSynth("auto")
			elif current is None or current.name != desired:
				if not synthDriverHandler.setSynth(desired):
					synthDriverHandler.setSynth("auto")
			synth = synthDriverHandler.getSynth()
			if synth is not None and synth.name == SILENT_SYNTH:
				synth.rate = int(self.cfg.get("estimatedRate") or 50)
			elif synth is not None:
				if self.cfg.get("rate") is not None and synth.isSupported("rate"):
					synth.rate = int(self.cfg["rate"])
				if self.cfg.get("volume") is not None and synth.isSupported("volume"):
					synth.volume = int(self.cfg["volume"])
			audio = config.conf["audio"]
			if self.mode is ConnectionMode.FOLLOWER and not self.cfg["localSoundsWhenControlled"]:
				if self._savedSoundVolume is None:
					self._savedSoundVolume = audio["soundVolume"]
				audio["soundVolumeFollowsVoice"] = False
				audio["soundVolume"] = 0
			elif self._savedSoundVolume is not None:
				audio["soundVolume"] = self._savedSoundVolume
				self._savedSoundVolume = None
		except Exception:
			log.error("Unable to apply AssistBridge audio policy", exc_info=True)

	# Control messages from AssistBridge

	def handleControl(self, name: str, msg: dict[str, Any]) -> None:
		handler = getattr(self, f"_control_{name}", None)
		if handler is None:
			log.debug(f"Unknown AssistBridge control message {name!r}")
			return
		try:
			handler(msg)
		except Exception:
			log.error(f"Error handling AssistBridge control message {name!r}", exc_info=True)

	def handleRemoteLine(self, line: bytes) -> None:
		transport = self.transport
		if transport is not None:
			transport.parse(line)

	def _control_config(self, msg: dict[str, Any]) -> None:
		values = msg.get("values") or {}
		for key in DEFAULT_CONFIG:
			if key in values:
				self.cfg[key] = values[key]
		self._bindConfiguredGestures()
		self._applyAudioPolicy()

	def _control_session_start(self, msg: dict[str, Any]) -> None:
		self.stopSession(silent=True)
		mode = ConnectionMode(msg["mode"])
		self.mode = mode
		transport = ClientLinkTransport(
			self.link,
			address=(msg.get("hostname") or "localhost", int(msg.get("port") or 6837)),
			channel=msg.get("key") or "",
			connectionType=mode.value,
		)
		if mode is ConnectionMode.LEADER:
			self.session = LeaderSession(localMachine=self.localMachine, transport=transport)
			transport.transportClosing.register(self._onLeaderClosing)
			if self.cfg["muteOnLocalControl"] and not self.localMachine.isMuted:
				self.localMachine.isMuted = True
		else:
			self.session = FollowerSession(localMachine=self.localMachine, transport=transport)
		self.transport = transport
		self._applyAudioPolicy()
		self._sendState()

	def _control_session_stop(self, msg: dict[str, Any]) -> None:
		self.stopSession(silent=bool(msg.get("silent", True)))

	def _control_net_state(self, msg: dict[str, Any]) -> None:
		transport = self.transport
		if transport is None:
			return
		connected = bool(msg.get("connected"))
		if not connected and self.session is not None:
			# Everyone in the channel is gone as far as we are concerned; the relay
			# will send a fresh channel_joined with the current clients after reconnecting.
			if isinstance(self.session, FollowerSession):
				self.session.leaders.clear()
				self.session.followers.clear()
				self.session.unregisterCallbacks()
			else:
				if self.sendingKeys:
					self._switchToLocalControl()
				self.session.followers.clear()
				self.session.leaders.clear()
				self.session.unregisterCallbacks()
		transport.setNetworkConnected(connected)
		self._sendState()

	def _control_toggle_control(self, msg: dict[str, Any]) -> None:
		self.toggleRemoteKeyControl(None)

	def _control_set_control(self, msg: dict[str, Any]) -> None:
		remote = bool(msg.get("remote"))
		if remote != self.sendingKeys:
			self.toggleRemoteKeyControl(None)

	def _control_set_mute(self, msg: dict[str, Any]) -> None:
		if self.localMachine is None:
			return
		muted = bool(msg.get("muted"))
		if muted != self.localMachine.isMuted:
			self.toggleMute()

	def _control_toggle_mute(self, msg: dict[str, Any]) -> None:
		self.toggleMute()

	def _control_cancel_speech(self, msg: dict[str, Any]) -> None:
		speech.cancelSpeech()

	def _control_speak(self, msg: dict[str, Any]) -> None:
		"""Speak text through NVDA (used for test output and for reading AssistBridge notices aloud)."""
		text = str(msg.get("text") or "")
		if not text:
			return
		if msg.get("asLocalOutput"):
			# Behaves like any other NVDA message on this computer (speech and braille):
			# forwarded to the controlling computer when being controlled.
			import ui

			ui.message(text)
			return
		self._allowLocalSpeechDepth += 1
		try:
			speech.speakMessage(text, priority=Spri.NOW)
		finally:
			self._allowLocalSpeechDepth -= 1

	def _control_open_settings(self, msg: dict[str, Any]) -> None:
		import gui

		panel = msg.get("panel") or "speech"
		commands = {
			"speech": "onSpeechSettingsCommand",
			"braille": "onBrailleSettingsCommand",
			"voice": "onSpeechSettingsCommand",
			"log": "onViewLogCommand",
			"speechViewer": "onToggleSpeechViewerCommand",
			"brailleViewer": "onToggleBrailleViewerCommand",
			"general": "onNVDASettingsCommand",
			"keyboard": "onKeyboardSettingsCommand",
			"audio": "onAudioSettingsCommand",
			"gestures": "onInputGesturesCommand",
			"selectBraille": "onSelectBrailleDisplayCommand",
			"selectSynth": "onSelectSynthesizerCommand",
		}
		method = getattr(gui.mainFrame, commands.get(panel, "onNVDASettingsCommand"), None)
		if method is None:
			method = gui.mainFrame.onNVDASettingsCommand
		method(None)

	def _control_quit(self, msg: dict[str, Any]) -> None:
		self.stopSession(silent=True)
		if self.link is not None:
			self.link.close()
		core.triggerNVDAExit()

	def _control_ping(self, msg: dict[str, Any]) -> None:
		if self.link is not None:
			self.link.sendControl("pong", nonce=msg.get("nonce"))

	# Session management

	def stopSession(self, silent: bool = True) -> None:
		if self.sendingKeys:
			try:
				self._switchToLocalControl(announce=not silent)
			except Exception:
				log.debugWarning("Error leaving remote control", exc_info=True)
		session, self.session = self.session, None
		self.transport = None
		if session is not None:
			try:
				if isinstance(session, FollowerSession):
					_brailleExtensions.filter_displayDimensions.unregister(
						self.localMachine._handleFilterDisplayDimensions,
					)
					self.localMachine.setBrailleDisplaySize([])
				else:
					session.unregisterBrailleInput()
				session.close()
			except Exception:
				log.debugWarning("Error closing session", exc_info=True)
		if self.localMachine is not None:
			self.localMachine.isMuted = False
			if self.localMachine.receivingBraille:
				self.localMachine.receivingBraille = False
		self.mode = None
		self.keyModifiers = set()
		self._applyAudioPolicy()
		self._sendState()

	def _onLeaderClosing(self) -> None:
		self.sendingKeys = False
		self.keyModifiers = set()

	def _sendState(self) -> None:
		if self.link is None:
			return
		session = self.session
		state: dict[str, Any] = {
			"mode": self.mode.value if self.mode else None,
			"sendingKeys": self.sendingKeys,
			"muted": bool(self.localMachine and self.localMachine.isMuted),
			"receivingBraille": bool(self.localMachine and self.localMachine.receivingBraille),
			"brailleDisplay": _brailleName(),
			"brailleCells": _brailleCells(),
		}
		if session is not None:
			state["leaders"] = len(session.leaders)
			state["followers"] = len(session.followers)
		synth = synthDriverHandler.getSynth()
		state["synth"] = synth.name if synth else None
		self.link.sendControl("state", **state)

	# Leader behaviour (adapted from _remoteClient.client.RemoteClient)

	@property
	def _leaderSession(self) -> LeaderSession | None:
		return self.session if isinstance(self.session, LeaderSession) else None

	def _processKeyInput(
		self,
		vkCode: int | None = None,
		scanCode: int | None = None,
		extended: bool | None = None,
		pressed: bool | None = None,
	) -> bool:
		if not self.sendingKeys or self._leaderSession is None:
			return True
		keyCode = (vkCode, extended)
		if not pressed and keyCode in self.hostPendingModifiers:
			self.hostPendingModifiers.discard(keyCode)
			return True
		if not pressed and keyCode == self.hostPendingNonmodifier:
			self.hostPendingNonmodifier = None
			return True
		gesture = KeyboardInputGesture(self.keyModifiers, keyCode[0], scanCode, keyCode[1])
		if gesture.isModifier:
			if pressed:
				self.keyModifiers.add(keyCode)
			else:
				self.keyModifiers.discard(keyCode)
		elif pressed:
			script = gesture.script
			if script in self._localScripts:
				wx.CallAfter(script, gesture)
				return False
		self.localMachine._dismissLocalBrailleMessage()
		self._leaderSession.transport.send(
			RemoteMessageType.KEY,
			vk_code=vkCode,
			extended=extended,
			pressed=pressed,
			scan_code=scanCode,
		)
		return False

	def toggleRemoteKeyControl(self, gesture: KeyboardInputGesture | None) -> None:
		session = self._leaderSession
		if session is None:
			if self.mode is ConnectionMode.FOLLOWER:
				self.announce("Not the controlling computer")
			else:
				self.announce("Not connected")
			return
		if not session.transport.connected and not self.sendingKeys:
			self.announce("Not connected")
			return
		if session.connectedFollowersCount < 1 and not self.sendingKeys:
			self.announce("No controlled computers are connected")
			return
		if self.sendingKeys:
			self._switchToLocalControl()
		else:
			self._switchToRemoteControl(gesture)

	def _switchToLocalControl(self, announce: bool = True) -> None:
		self.sendingKeys = False
		self._setReceivingBraille(False)
		self._releaseKeys()
		if announce:
			self.announce("Controlling local computer")
		if self.cfg["muteOnLocalControl"] and self.localMachine and not self.localMachine.isMuted:
			self.localMachine.isMuted = True
		self._sendState()

	def _switchToRemoteControl(self, gesture: KeyboardInputGesture | None) -> None:
		self.sendingKeys = True
		self._setReceivingBraille(True)
		if gesture is not None:
			self.hostPendingModifiers = set(gesture.modifiers)
			self.hostPendingNonmodifier = (gesture.vkCode, gesture.isExtended)
		else:
			self.hostPendingModifiers = set()
			self.hostPendingNonmodifier = None
		self.announce("Controlling remote computer")
		if self.localMachine.isMuted:
			self.localMachine.isMuted = False
		self._sendState()

	def _releaseKeys(self) -> None:
		session = self._leaderSession
		if session is None:
			self.keyModifiers = set()
			return
		if canModifiersPerformAction(KeyboardInputGesture._generalizeModifiers(self.keyModifiers)):
			session.transport.send(RemoteMessageType.KEY, vk_code=winUser.VK_NONE, extended=False, pressed=True)
			session.transport.send(RemoteMessageType.KEY, vk_code=winUser.VK_NONE, extended=False, pressed=False)
		for vk, ext in self.keyModifiers:
			session.transport.send(RemoteMessageType.KEY, vk_code=vk, extended=ext, pressed=False)
		self.keyModifiers = set()

	def _setReceivingBraille(self, state: bool) -> None:
		session = self._leaderSession
		if session is None or self.localMachine is None:
			return
		if state and session.callbacksAdded and braille.handler.enabled:
			session.registerBrailleInput()
			self.localMachine.receivingBraille = True
		elif not state:
			session.unregisterBrailleInput()
			self.localMachine.receivingBraille = False

	def _sessionLockStateChangeHandler(self, isNowLocked: bool) -> None:
		if self._leaderSession is None:
			return
		if isNowLocked and self.sendingKeys:
			self._wasSendingKeysBeforeLock = True
			self._switchToLocalControl()
		elif not isNowLocked and self._wasSendingKeysBeforeLock:
			self._wasSendingKeysBeforeLock = False
			self._switchToRemoteControl(None)

	def toggleMute(self) -> None:
		if self.session is None:
			self.announce("Not connected")
			return
		if self._leaderSession is None:
			self.announce("Not the controlling computer")
			return
		self.localMachine.isMuted = not self.localMachine.isMuted
		self.announce("Muted remote" if self.localMachine.isMuted else "Unmuted remote")
		self._sendState()

	# Scripts

	def _bindConfiguredGestures(self) -> None:
		self.clearGestureBindings()
		bindings = {
			"toggleControl": self.cfg.get("toggleGestures") or [],
			"sendSAS": self.cfg.get("sasGestures") or [],
			"pushClipboard": self.cfg.get("pushClipboardGestures") or [],
			"toggleMute": self.cfg.get("toggleMuteGestures") or [],
		}
		for scriptName, gestures in bindings.items():
			for gestureId in gestures:
				try:
					self.bindGesture(gestureId, scriptName)
				except Exception:
					log.warning(f"Unable to bind {gestureId!r} to {scriptName}", exc_info=True)

	@scriptHandler.script(description="Switches whether the keyboard controls this computer or the remote computer")
	def script_toggleControl(self, gesture):
		if self.session is None:
			# Not in a session: let the keystroke through so it behaves as it normally would.
			gesture.send()
			return
		self.toggleRemoteKeyControl(gesture)

	@scriptHandler.script(description="Sends control+alt+delete to the controlled computer")
	def script_sendSAS(self, gesture):
		session = self._leaderSession
		if session is None:
			self.announce("Not the controlling computer")
			return
		session.transport.send(RemoteMessageType.SEND_SAS)

	@scriptHandler.script(description="Sends the clipboard text to the other computer")
	def script_pushClipboard(self, gesture):
		if self.session is None:
			gesture.send()
			return
		if self.link is not None:
			self.link.sendControl("request", action="push_clipboard")

	@scriptHandler.script(description="Mutes or unmutes speech and sounds from the remote computer")
	def script_toggleMute(self, gesture):
		self.toggleMute()


def _prepareBundledConfig() -> None:
	"""Keep the bundled NVDA quiet and unobtrusive for the person at this computer.

	Runs before NVDA shows its startup dialogs, so they are never displayed.
	"""
	values = {
		("general", "showWelcomeDialogAtStartup"): False,
		("general", "askToExit"): False,
		("general", "playStartAndExitSounds"): False,
		("update", "askedAllowUsageStats"): True,
		("update", "allowUsageStats"): False,
		("update", "autoCheck"): False,
		("update", "startupNotification"): False,
		# NVDA's own Remote Access must stay off; this plugin runs the sessions instead.
		("remote", "enabled"): False,
		("audio", "audioDuckingMode"): 0,
	}
	for (section, key), value in values.items():
		try:
			config.conf[section][key] = value
		except Exception:
			log.debug(f"Unable to set {section}.{key}", exc_info=True)


def _brailleName() -> str | None:
	try:
		return braille.handler.display.name if braille.handler and braille.handler.display else None
	except Exception:
		return None


def _brailleCells() -> int:
	try:
		return int(braille.handler.displaySize) if braille.handler else 0
	except Exception:
		return 0
