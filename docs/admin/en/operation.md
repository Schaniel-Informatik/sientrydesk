# Operation

For admins: use (also as a basis for introducing it to the users), testing, yearly renewal of the credentials,
troubleshooting. Installation in [installation.md](installation.md), distribution in [intune.md](intune.md). German
version: [../betrieb.md](../betrieb.md). The texts for the users exist in German, English and Polish
([Language](installation.md#language)), the English ones are quoted here.

## Use
Bell icon at the bottom right, possibly under the arrow ^ in the taskbar.
- **Left click:** status, the doors with their ring setting (a click opens the live view), "Pause doorbell"
  (15 / 30 / 60 / 120 minutes) and "Settings …", which opens the same menu as the right click.
- **Right click:** version, status, each door with its ring setting, "Pause doorbell", "Switch on door sound
  automatically", test ring, check, exit.

When someone rings, a window appears on top of all others, with ringtone, live view and an "Open" button. If someone
answers in the mobile app, it closes everywhere ("Answered elsewhere"). **If someone talks at another PC** (from
0.9.0), the other PCs show in orange "● mmuster is talking (PC FRONTDESK-01)", and the ringtone stops. The window stays
open, talking and opening still work, in case that person walks away. Shown is who pressed first. On the PC where
someone talks nothing changes. This only works between PCs in the same network segment
([installation.md](installation.md#prerequisites)).

**Ringing per door** (right click or left click → Settings …, then door → choice; applies to the signed-in user): per
door "immediately" (default), "after 10 s" or "off". The setting is shown in the menu with the door, e.g.
"Door 2: off". With "after 10 s" the icon blinks first, after 10 s window and ringtone appear. If someone else answers
in this time (also by talking at another PC) or the visitor hangs up, nothing appears. With a click on the icon →
"Show now" the window comes immediately. With "off" nothing appears for this door, the live view without ringing stays
in the menu. Calls not shown this way do not count as missed. Deviations are also shown in the text at the icon (mouse
over it). Example: at the front desk the main door rings immediately, a side door after 10 s. This is a convenience,
not a lock. If a door should not be available on a PC at all (not even for opening), it belongs into `Doors` of
sientrydesk.json ([installation.md](installation.md)).

"Sound on" in the ring window switches on the sound of the door and switches off the ringtone. After the end of a call
the window stays open for 10 s. If the visitor hung up (e.g. pressed twice) or nobody answered, opening is still
possible in this time.

**Talkback:** press and hold "Talk – press and hold", talk, release. You can start talking right when pressing,
nothing is lost until the connection to the door is up. After releasing, the microphone records 0.3 s more, so that
the last word is not cut off. The sound of the door comes with a delay of about 1 s. **With a headset** the sound of
the door stays on while talking and plays in the headset (communications device of Windows, as with Teams), so you also
hear the visitor when both talk at the same time. The ringtone stays on the default device. With a headset you may hear
your own voice back from the door about 1 s later (echo through speaker and microphone of the door station). **With
speakers** the sound of the door is muted while the microphone records, otherwise the visitor would hear themselves as
an echo. What they say in this time cannot be heard at the PC, so wait briefly after releasing. Whether a headset is
detected is shown in the menu (right click) at "Talk with: Headset (detected)" or "Speakers (detected)" and in
"Check …". Some USB headsets report themselves to Windows as speakers, then choose "Headset – hear the door while
talking" there. "Speakers – door muted while talking" helps if the visitor hears an echo. After 60 s in one go talking
ends by itself. Possible during the call and in the 10 s after, not if someone else answered or declined. If two talk
at the same time despite the display, both voices arrive at the door. The mobile apps do not learn that someone talks
at a PC and keep ringing until someone opens or the call ends. During a Teams meeting Windows may dampen other sounds
(Sound → More sound settings → Communications).

| Icon | Meaning |
|---|---|
| green | ready |
| blue | doorbell paused, no window and no sound on this PC. Missed calls are listed in the menu |
| grey | console not reachable, e.g. outside the company network without VPN. No notice |
| orange | ringing works, but something else does not (live view), or the credentials expire within 14 days. Then a notice also appears once a day |
| red | **this PC does not ring** (service, token, certificate). After 30 s a notice appears |

## Testing after the installation
1. **Icon green.**
2. **Test ring** (right click): this PC only, without live view, opening blocked. If a door is set to "after 10 s",
   the icon blinks first.
3. **Live view without ringing:** left click → choose a door. Without `DoorCameras` in the configuration a door appears
   only after its first ring.
4. **Real ring:** window, ringtone, after 1–2 s the live view.
5. **Answer on the phone:** the window closes with "Answered elsewhere".
6. **Open at the PC**, only with someone at the door. The Access log shows "<user> via SI EntryDesk (<PC>)".
7. **Pause:** pause for 15 minutes, let it ring. Nothing appears, afterwards the call is listed in the menu.
8. **Talkback**, only with someone at the door: hold the talk button, talk, release, hear the answer. Once with
   speakers, once with a headset.
9. **Two PCs:** talk at one PC. At the other "… is talking (PC …)" appears and the ringtone stops.

## Renew the credentials yearly
Without interruption, because the old credential stays valid until the end. Via Intune:
[intune.md](intune.md#renew-the-credentials-yearly). By hand:
1. Create a new Access token and a new Protect key ([permissions as for the installation](installation.md#create-the-credentials),
   1 year) and store them in the password manager.
2. On every PC: `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"`.
3. Enter the new expiry dates in sientrydesk.json and distribute it with `install.ps1`.
4. Check: icon green, live view from the menu, ring once.
5. Only then delete the old token and the old key.
6. Note it in the operations manual of the installation.

## Troubleshooting
**First check:** right click on the icon → "Check …" or `SIEntryDesk.exe --check`. The window is in English for
English and Polish installations. It shows per prerequisite green, orange or red, with one sentence on what to do:
service, autostart, app, coordination with other PCs (firewall rule "SI EntryDesk: coordination between PCs" for UDP
47801), sound output, microphone, talk with (headset or speakers), connection to the service and version on the PC;
configuration (with the date of the sientrydesk.json the service runs with), language, credentials, Access and Protect
(reachable, certificate, token and permissions, key), ring events, stream per door, talkback and expiry dates for the
installation. The service reads sientrydesk.json only at startup. If it has changed since, the line turns orange:
restart the service or the PC. If the console is not reachable, a line "Console: not reachable" appears after at most
about 10 s. It needs no admin rights and no input. "Copy result" puts the text without secrets on the clipboard, for a
ticket or an email. Whether the sound arrives at the door during talkback the check cannot tell.

| Icon | What to do |
|---|---|
| grey | Normal outside the company network. In the office: check network or VPN, ports 12445, 443, 7441 |
| orange | Read the text in the menu. Check the Protect key or renew the credentials |
| red | Read the text in the menu and the log: service not running, token rejected, certificate does not match the pin |

| Message when talking | What to do |
|---|---|
| no "Talk" button | talkback switched off on this PC (`Talkback`), Protect key missing or the camera of the door is unknown |
| "No access to the microphone" | Windows: Privacy → Microphone → allow desktop apps |
| "No microphone found" | connect a microphone or headset, set it in Windows as default communications device |
| button works, nothing arrives at the door | UDP 7004 from the PC to the door station blocked; the log names the address of the door station |
| "Protect key rejected" / "Protect not reachable" | as for the live view |
| visitor hears themselves as an echo | sound of the door comes from speakers, but "Headset" chosen or wrongly detected: in the menu "Talk with" → "Speakers – door muted while talking" |
| another PC does not show who is talking | "Check …" on both PCs: firewall rule missing → run install.ps1 again. Otherwise another network segment, another Access token, or the clocks of the PCs differ by more than 60 s (shown in the service log) |
| part of the visitor's answer is missing | normal with speakers (door muted while talking). With a headset: menu "Talk with" shows "Speakers (detected)" → choose "Headset – hear the door while talking" |

**Live view black or silent:** The log of the video player is at the user under `%LOCALAPPDATA%\SIEntryDesk\vlc.log`.
"no … module matched" means that a part of the player is missing.

**Service log** (readable only by administrators): `C:\ProgramData\SIEntryDesk\logs\service-YYYYMMDD.log`, German for
German installations, otherwise English. It contains door names, but no tokens and no stream addresses. Installation
via Intune: `C:\Windows\Temp\SIEntryDesk-install.log`, more in [intune.md](intune.md#control).

**Check the configuration:** `SIEntryDesk.exe --setup` with "Load installed configuration", see
[installation.md](installation.md#with-the-setup-wizard).

## Security and limits
- **The permissions of UniFi Access do not apply.** Whoever is signed in at a PC with SI EntryDesk can open during a
  call. Access control is the choice of PCs, see [installation.md](installation.md#who-may-open-the-pc-not-the-person).
- Whoever is **local administrator** on a PC can read the credentials from `secrets.dat` and open all doors with them.
  Standard users cannot. Therefore: no local admin rights for users, BitLocker, LAPS.
- The app opens only during a call or up to 10 s after it (after "Visitor hung up" or "Nobody answered"). The service
  enforces this. Whoever uses a token outside the app is not bound by it.
- Every opening is in the Access log with user and PC, every live view request in the service log.
- The message "… is talking" goes unencrypted into the local network and contains user, PC and call, but no door and no
  secrets. It is signed: only someone with the Access token can forge it, and with that one could open anyway. It does
  nothing more than the display and stopping the ringtone.
- The live view runs only via the checked TLS connection of the service. The app gets a one-time address on 127.0.0.1
  that ends with the call or after the set duration.
