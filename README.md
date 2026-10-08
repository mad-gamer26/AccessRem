# AssistBridge

Remote assistance for Windows that is fully compatible with **NVDA Remote Access** (built into NVDA 2025.1 and later) and the **NVDA Remote** add-on. It's designed for sighted people who need help from, or want to help, someone who uses NVDA.

AssistBridge uses the same approach as Remote Incident Manager (RIM): it bundles its own copy of NVDA as a backend. That NVDA reads the computer and produces speech, sounds and braille. AssistBridge carries all of that to and from the other computer over the NVDA Remote protocol. The helper hears exactly what they would hear from NVDA on their own computer.

## Features

| | |
|---|---|
| **Get help** (be controlled) | The bundled NVDA reads this computer to the helper, using speech, tones, sounds and braille. Keys and braille input from the helper are carried out here. A silent relay voice keeps say all paced naturally. You don't hear the speech unless you turn that on. |
| **Give help** (control) | Remote speech is spoken here by NVDA with a real voice and shown in a live transcript. The remote braille line appears on your braille display and as Unicode braille on screen. Send your keyboard to the remote computer with Ctrl+T (or NVDA+Alt+Tab) and bring it back with Ctrl+Alt+Shift+F11 (or NVDA+Alt+Tab). Mute is included. |
| **Saved computers** | Save any number of "computers": a name, server or host, port and key, plus the action Enter performs and an optional auto-connect. Keys are encrypted with Windows DPAPI. |
| **Servers** | Use relay servers such as `nvdaremote.com`, connect directly to a computer that is hosting, or **host a direct connection** on this computer. Hosting uses a built-in relay with a self-signed certificate and includes an external IP and port check. |
| **Keys** | Generate keys from the relay server, or random keys when hosting. |
| **Links** | Open `nvdaremote://` links (optionally as the system handler). Copy a link or a ready-to-send invitation. |
| **Clipboard** | Send and receive clipboard text. |
| **Ctrl+Alt+Del** | Send it to the remote computer. To receive it, install the optional helper service (Windows only lets a service simulate it). |
| **Security** | TLS 1.2+. Unverifiable certificates show their SHA-256 fingerprint, with "connect once" or "trust always" choices, the same as NVDA. |
| **Protocol** | Protocol version 2, plus version 1 peers on the hosted relay. Handles message of the day, version mismatch, wrong key, and automatic reconnection every 5 seconds. |
| **Accessibility** | Every control is named and keyboard reachable, with access keys, shortcuts, live-region announcements and a strong focus ring. The app switches to Windows contrast themes automatically and follows Windows text size. |
| **Tray** | A notification-area icon with session actions and notifications. You can start AssistBridge with Windows. |

## Building

Requirements: Windows 10/11 x64, the .NET 8 SDK, 7-Zip (to unpack the NVDA launcher) and Python 3 (tests only).

```powershell
.\build.ps1              # app + add-on + downloads and bundles NVDA 2026.2 into dist\AssistBridge
.\build.ps1 -Zip         # also produces dist\AssistBridge-<version>.zip
.\build.ps1 -SkipNvda    # rebuild only the app and add-on
```

Run `dist\AssistBridge\AssistBridge.exe`. The folder is portable and can be copied anywhere.

## Tests

```powershell
dotnet test tests\AssistBridge.Tests
```

The test suite includes:

- **Unit tests:** URLs, addresses, keys, settings encryption and the message of the day.
- **Local relay routing:** routing, wrong-key rejection, certificate pinning and trust, and protocol version 1 peers.
- **TLS interop:** a Python client that connects exactly the way NVDA's transport does (`ssl.PROTOCOL_TLSv1_2`).
- **Live interop:** key generation and relaying through the public `nvdaremote.com` server. Set `ASSISTBRIDGE_SKIP_NETWORK=1` to skip this test.
- **End to end:** these tests run the real bundled NVDA.
  - The follower test checks that NVDA's speech and braille reach the leader.
  - The leader test covers NVDA's leader session, control toggling, mute and Ctrl+Alt+Del.
  - NVDA is started on a separate, invisible Windows desktop, so it never replaces a screen reader that is already running.
  - Set `ASSISTBRIDGE_SKIP_BACKEND=1` to skip these tests.

## How it fits together

```
 AssistBridge.exe (WPF)                         bundled NVDA (portable, --minimal --no-sr-flag)
 ├─ NetworkSession  ── TLS ──► relay server     └─ backend add-on
 ├─ LocalRelayServer (direct connections)          ├─ NVDA's own LeaderSession / FollowerSession / LocalMachine
 ├─ SessionController ◄── loopback JSON link ──►   ├─ key capture, control toggle, mute (from NVDA's RemoteClient)
 └─ UI, tray, settings, links, clipboard, SAS      └─ silent relay synth; local speech/sound policy
```

The add-on gives NVDA's built-in `_remoteClient` sessions a transport whose other end is AssistBridge, so every speech sequence, tone, wave, braille cell and key is serialised by NVDA itself. AssistBridge forwards those lines to and from the relay. It handles connection-level messages itself: join, message of the day, errors, clipboard and Ctrl+Alt+Del.

The bundled NVDA runs only while a session is active. If NVDA is already running, AssistBridge asks before temporarily replacing it, then restarts it after the session.

## Silence on the computer being helped

When this computer is being controlled, AssistBridge writes the session role into a private link file before starting NVDA. The add-on switches NVDA to the silent relay voice and mutes NVDA's sounds as it loads, before NVDA says anything. The only sounds the person hears are AssistBridge's connection cues, which can be turned off in Settings › General.

## Resilience

The network connection belongs to AssistBridge, so the bundled NVDA can be replaced without dropping the session:

- **NVDA can't be closed by accident.** NVDA+Q, NVDA's Exit menu item and the exit dialog are refused during a session. The refusal is announced and AssistBridge is brought forward.
- **Restart requests are handled by AssistBridge.** If NVDA asks to restart itself, AssistBridge restarts it instead.
- **Crashes are recovered.** If NVDA stops unexpectedly, AssistBridge restarts it and replays the session state: who is connected, and the helpers' braille display sizes.
  - This allows up to 3 restarts within 2 minutes.
  - The other computer hears a short gap, not a disconnect.

## Installed mode: User Account Control and sign-in screens

**Settings › Getting help › Install for all users** (administrator) sets up the following:

1. Copies AssistBridge to `C:\Program Files\AssistBridge`.
2. Makes `nvda\nvda.exe` a copy of NVDA's signed UI Access build. NVDA's own installer does the same.
3. Registers that copy with Windows Ease of Access under its own name (`assistbridge_nvda`), so a separately installed NVDA's registration is never touched.
4. Writes a `systemConfig` for secure screens: NVDA Remote Access on, and silent unless "Speak on this computer too" is chosen.
5. Installs the AssistBridge Helper service. It handles Ctrl+Alt+Del, and applies the speak-locally choice to secure screens.
6. Adds a Start menu shortcut and an entry in Windows' installed apps list (for uninstalling).

During a session as the controlled computer, the installed copy works like this:

1. AssistBridge runs the same signed executable on the user's desktop.
2. It tells the Ease of Access broker that this screen reader is running. A separately installed NVDA is held back for the session, because it couldn't join and would only speak aloud.
3. When a UAC prompt appears, the add-on runs NVDA's own secure desktop handshake: a private local relay, plus connection details in shared memory.
4. The NVDA that Windows starts on the secure screen joins the session through that handshake, so the helper hears and operates the prompt.
5. NVDA checks that both copies are the same executable before connecting.

## Limitations

- Without installed mode, the bundled NVDA behaves like a portable NVDA. It can't read secure screens, or programs running as administrator.
- Receiving Ctrl+Alt+Del requires the helper service, which installed mode includes. It can also be installed on its own.

## License

GPL-2.0-or-later. See `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`. AssistBridge is an independent project and isn't made or endorsed by NV Access.
