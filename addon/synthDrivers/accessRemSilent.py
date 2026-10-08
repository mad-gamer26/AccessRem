# AccessRem silent relay synthesizer.
# Copyright (C) 2026 AccessRem contributors.
# This file is covered by the GNU General Public License, version 2 or later.

"""A synthesizer that produces no audio but behaves like a real one.

When this computer is being controlled, its speech is forwarded to the controlling
computer, so the sighted person at this computer should not hear it. NVDA's own
"No speech" synthesizer never reports index or done-speaking notifications, which
stalls say all and other index driven features. This driver estimates how long the
remote voice will take to speak each chunk and raises the notifications at roughly
that pace, so say all advances naturally on the controlling computer.
"""

import threading
import time
from collections import OrderedDict

import synthDriverHandler
from speech.commands import (
	BreakCommand,
	CharacterModeCommand,
	IndexCommand,
	LangChangeCommand,
	PhonemeCommand,
	PitchCommand,
	RateCommand,
	VolumeCommand,
)
from synthDriverHandler import SynthDriver as _BaseSynthDriver
from synthDriverHandler import synthDoneSpeaking, synthIndexReached


class SynthDriver(_BaseSynthDriver):
	name = "accessRemSilent"
	description = "AccessRem silent relay"

	supportedSettings = (_BaseSynthDriver.RateSetting(),)
	supportedCommands = {
		IndexCommand,
		CharacterModeCommand,
		LangChangeCommand,
		BreakCommand,
		PitchCommand,
		RateCommand,
		VolumeCommand,
		PhonemeCommand,
	}
	supportedNotifications = {synthIndexReached, synthDoneSpeaking}

	@classmethod
	def check(cls):
		return True

	def __init__(self):
		super().__init__()
		self._rate = 50
		self._lock = threading.Condition()
		self._queue: list[tuple[float, int | None]] = []
		self._generation = 0
		self._running = True
		self._thread = threading.Thread(target=self._run, name="accessRemSilentTimer", daemon=True)
		self._thread.start()

	def terminate(self):
		with self._lock:
			self._running = False
			self._queue.clear()
			self._lock.notify_all()
		super().terminate()

	def _get_rate(self):
		return self._rate

	def _set_rate(self, value):
		self._rate = max(0, min(100, int(value)))

	def _get_voice(self):
		return "default"

	def _getAvailableVoices(self):
		return OrderedDict(default=synthDriverHandler.VoiceInfo("default", "Default"))

	def _secondsPerCharacter(self) -> float:
		# Map 0..100 to roughly 8..40 characters per second (about 100 to 480 words per minute).
		charsPerSecond = 8 + (self._rate / 100.0) * 32
		return 1.0 / charsPerSecond

	def speak(self, speechSequence):
		perChar = self._secondsPerCharacter()
		with self._lock:
			# Continue after anything still queued so consecutive utterances do not overlap.
			start = self._queue[-1][0] if self._queue else time.monotonic()
			elapsed = 0.0
			charMode = False
			for item in speechSequence:
				if isinstance(item, str):
					if charMode:
						elapsed += 0.25 * len(item.strip() or " ")
					else:
						elapsed += perChar * len(item)
				elif isinstance(item, IndexCommand):
					self._queue.append((start + elapsed, item.index))
				elif isinstance(item, BreakCommand):
					elapsed += item.time / 1000.0
				elif isinstance(item, CharacterModeCommand):
					charMode = item.state
			# None marks the end of an utterance (done speaking).
			self._queue.append((start + elapsed, None))
			self._lock.notify_all()

	def cancel(self):
		with self._lock:
			self._generation += 1
			self._queue.clear()
			self._lock.notify_all()

	def pause(self, switch):
		# Nothing is playing, so there is nothing to pause. Pausing simply stops the clock.
		if switch:
			self.cancel()

	def _run(self):
		while True:
			with self._lock:
				if not self._running:
					return
				if not self._queue:
					self._lock.wait()
					continue
				due, index = self._queue[0]
				delay = due - time.monotonic()
				if delay > 0:
					self._lock.wait(delay)
					continue
				self._queue.pop(0)
				generation = self._generation
				remaining = bool(self._queue)
			# Notify outside the lock; NVDA's speech manager queues its own handling.
			if generation != self._generation:
				continue
			if index is not None:
				synthIndexReached.notify(synth=self, index=index)
			elif not remaining:
				synthDoneSpeaking.notify(synth=self)
