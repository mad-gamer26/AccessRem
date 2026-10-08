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

## Limitations

- Like any portable copy of NVDA, the bundled NVDA can't read or operate User Account Control and sign-in secure screens. It also can't control programs running as administrator unless AssistBridge itself runs as administrator.
- Receiving Ctrl+Alt+Del requires the optional helper service (Settings › Getting help). Installing it needs administrator permission.

## License

GPL-2.0-or-later. See `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`. AssistBridge is an independent project and isn't made or endorsed by NV Access.
