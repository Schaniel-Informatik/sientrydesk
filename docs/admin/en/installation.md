# Installation

For admins. German version: [../installation.md](../installation.md). There are two ways: **by hand** on single PCs
(this guide) or **via Intune** to a device group ([intune.md](intune.md)). Both need the same prerequisites, the same
credentials and the same `sientrydesk.json`. Operation, renewal and troubleshooting are in [operation.md](operation.md).

Anything specific to an installation (addresses, names, which password vault holds the credentials) belongs into the
operations manual of that installation, not here.

## What is where
| What | Where | Content |
|---|---|---|
| Service `SIEntryDesk` | `C:\Program Files\SIEntryDesk\Service` | Holds the credentials, connects to Access and Protect, decides about opening, passes on the live view. Runs as `NT SERVICE\SIEntryDesk` |
| Tray app `SIEntryDesk.exe` | `C:\Program Files\SIEntryDesk\App` | Ring window, live view, "Open" button. Knows no credentials |
| Scripts for admins | `C:\Program Files\SIEntryDesk` | `set-tokens.ps1` (renew credentials), `uninstall.ps1`, with Intune `detect.ps1` |
| Installation configuration | `C:\ProgramData\SIEntryDesk\sientrydesk.json` | Console, certificate pins, doors, expiry dates, language. No secrets |
| Credentials | `C:\ProgramData\SIEntryDesk\secrets.dat` | Access token and Protect key, encrypted, see [below](#where-the-credentials-are-stored) |
| Log | `C:\ProgramData\SIEntryDesk\logs` | Rings, openings, live view requests with user. No credentials, no stream addresses |
| Version, label of the credentials | `HKLM\SOFTWARE\SIEntryDesk` | `Version`, with Intune `TokenLabel`. No secrets |

## Prerequisites
- **PC:** Windows 11, 64-bit. Local administrator for the installation. For talkback a microphone, preferably a
  headset, and in Windows under Privacy → Microphone "Let desktop apps access your microphone" (default).
- **UniFi OS console** with UniFi Access and UniFi Protect.
- **UniFi Access** with Developer API (version 1.20.11 or newer, not with Identity Enterprise): ring events and
  opening.
- **UniFi Protect** with Integration API (API key under UniFi OS → Settings → Control Plane → Integrations): live view
  and camera names. Without it everything works except the live view.
- **Door stations** (e.g. G6 Entry, G6 Pro Entry) in Protect, assigned in Access to a door with UA Hub Door.
- **Network:** The PCs reach the console on TCP 12445 (Access), 443 (Protect) and 7441 (stream). For talkback also
  **UDP 7004 directly to the door stations**, not to the console: Protect names the door station as the target for the
  audio. This audio is not encrypted, that is how Ubiquiti built the interface. The app needs no inbound connections,
  the installer blocks them in the Windows firewall.
- **All PCs with SI EntryDesk in the same network segment (subnet).** From 0.9.0 the services tell each other by UDP
  broadcast on **port 47801** who answered a call. Routers do not forward broadcasts: a PC in another segment rings and
  opens normally, but does not show who talks at another PC and is not shown itself. The installer allows the port in
  the Windows firewall only from the local subnet and only for the service. The messages are signed with a key derived
  from the Access token. All PCs therefore need the same Access token (as usual with the credentials package).

## Language
From 0.10.0 `Language` in sientrydesk.json sets the language, German by default:

| Part | Language |
|---|---|
| Users: tray menu, ring window, notifications | `de` German, `en` English, `pl` Polish |
| Admins: "Check" window, service log | German for `de`, otherwise English |
| Setup wizard, `--help` | Language of Windows on this PC: German, otherwise English |
| Scripts (`install.ps1`, `set-tokens.ps1`, credentials package) | English |
| Admin guides | `Doku\` German, `Doku\en\` English |

The app cannot read sientrydesk.json. It learns the language from the service and keeps it per user. Until the first
connection after the installation the language of Windows applies.

## Who may open: the PC, not the person
**The people, roles and door permissions of UniFi Access do not apply to SI EntryDesk.** The official Access Developer
API knows no sign-in of single users. It works with an API token, and that token applies to the whole installation:
*Location = Edit* allows opening **every door**. This cannot be restricted.

This means:
- **Everyone signed in at a PC with SI EntryDesk can open during a call**, whether or not they have permission for this
  door in Access or are set up as receivers of doorbell calls (Door Attendant, Doorbell Call Receiver). The permission
  results from **which PCs** the app is installed on. That is the real decision, with Intune via the device group.
- `Doors` in sientrydesk.json defines which doors a PC shows and opens. The service enforces this, the token itself
  could still open all.
- The Access log shows for every opening "<Windows user> via SI EntryDesk (<PC>)". SI EntryDesk reports this name,
  Access does not check it.
- Whoever has the token itself, for example a local administrator of the PC, can open every door at any time, without
  a call.

As soon as Ubiquiti offers an API with permissions per user, SI EntryDesk can use it. Until then the choice of PCs is
the access control.

## Preparation in UniFi
Once per installation, before the first installation.

### Create the credentials
| # | Where | Name (suggestion) | Permissions | For |
|---|---|---|---|---|
| 1 | Access → Settings → General → **API Token** | `SIEntryDesk` | **Device = View**, **Location = Edit**, everything else **None** | Ring events and opening |
| 2 | UniFi OS → Settings → Control Plane → **Integrations** | `SIEntryDesk live view` | Permissions of the account it is created under | Live view and camera names |

The names of the permissions follow the German interface ("Gerät", "Standorte"). In an English interface they may be
named slightly differently.

- **One Access token for everything.** It receives the ring events and opens the door.
- **Valid for 1 year.** The expiry date belongs into sientrydesk.json as `TokenExpires` and `ProtectKeyExpires`. From
  14 days before, the icon turns orange, and every app reports once a day by when to renew.
- **The Access dialog suggests "Edit" for several areas.** Set everything except *Device* and *Location* to **None**.
  *Location = Edit* allows opening **all doors**, keeping them open permanently and triggering lockdown or evacuation.
  Access does not allow restricting this.
- **Protect key:** It has the Protect permissions of the account that creates it. Under an admin account it may do
  everything in Protect. Safer is a separate local user with a view-only role.
- **Storage:** only in the password manager, never in files, tickets or chats. If a value does end up there, delete
  the credential and create a new one.

### RTSPS stream per door station
The service gets the live view as an RTSPS stream from the door station. Protect only delivers it once it is switched
on for the camera, and that for **each door station separately**:
- **In Protect:** camera of the door station → Settings → Advanced → RTSP, switch on quality **Medium**. Higher
  quality brings nothing in the small window and costs bandwidth.
- **Or in the setup wizard** ([step 1](#with-the-setup-wizard)): it shows per door whether the stream exists and
  creates it at the push of a button.

The stream address contains a secret part and works like an access key to the camera image: do not pass it on, not in
documents, tickets or chats. SI EntryDesk does not pass it on to the users and does not write it into the log. If it
did get out: delete the stream in Protect and create it again, then a new address applies.

## The packages
The build delivers two ZIP files:

| File | Content | For |
|---|---|---|
| `SIEntryDesk-<version>-win-x64.zip` | `App\`, `Service\`, `install.ps1`, `uninstall.ps1`, `set-tokens.ps1`, `prepare-intune.ps1`, `sientrydesk.example.json`, `Doku\` | Installation by hand and Intune app "SI EntryDesk" |
| `SIEntryDesk-Tokens-<version>.zip` | `set-tokens-intune.ps1`, `prepare-intune.ps1`, `prepare-intune.cmd`, `tokens.example.txt` | only for the Intune app with the credentials ("SI EntryDesk Tokens"), own version, changes rarely |

For the installation by hand only the program package is needed.

Windows blocks PowerShell scripts from the internet. Therefore start every script like this:
`powershell -ExecutionPolicy Bypass -File .\<script>.ps1`. This applies only to this call and changes no setting on
the PC.

## Step 1: create sientrydesk.json
**The file must be ready before the installation.** `install.ps1` asks for no settings and stops without it with
"Configuration not found". It contains no secrets and applies to all PCs of an installation.

### With the setup wizard
The easiest way. On a Windows PC in the company network or via VPN, directly from the unpacked package, without
installation. It appears in the language of Windows (German, otherwise English):
```powershell
.\App\SIEntryDesk.exe --setup
```
It checks step by step:
1. Reachability of the console (ports 12445, 443, 7441), and shows the certificate fingerprints you confirm as pins.
2. The Access token: the needed permissions, a warning for unnecessary ones. It checks the permission to open on a door
   that does not exist.
3. The Protect key: version and cameras.
4. Doors and cameras: it suggests the mapping by the names and shows whether the RTSPS stream exists. On confirmation it
   creates it. "Test live view" shows the image via the same checked path as the app.
5. Settings: live view without ringing, duration, expiry dates.
6. Language for the users: Deutsch, English or Polski.
7. It saves sientrydesk.json.

The wizard uses tokens only for checking, it does not store them.

**Check an existing configuration:** at the top "Load installed configuration" (on a PC with SI EntryDesk, readable
only as administrator) or "Open another sientrydesk.json …", or directly:
```powershell
.\App\SIEntryDesk.exe --setup C:\path\to\sientrydesk.json
```
The wizard takes over the values and checks the console right away: whether the fingerprints still match the pins and
how long the credentials are valid. With Access token and Protect key it also compares doors and cameras with the file.
Saving is only needed if something changed.

### By hand
From the template `sientrydesk.example.json` in the package. Comments only on lines of their own.

| Field | Meaning |
|---|---|
| `Language` | Language for the users: `de` (default), `en` or `pl`, see [Language](#language) |
| `Host` | Name or IP of the console |
| `AccessPin`, `ProtectPin` | SHA-256 fingerprints of the certificates on ports 12445 and 443. Show them with `.\Service\SIEntryDesk.Service.exe show-pins <host>`, **only in the company network or via VPN** |
| `StreamPin` | only needed if port 7441 has a different certificate than 443 |
| `Doors` | Doors this PC shows (door ID or name). Empty = all |
| `DoorCameras` | Door ID → Protect camera ID. Without an entry the service learns the camera at the first ring |
| `LiveView`, `LiveViewSeconds` | Live view without ringing in the menu, duration 15–600 s (default 60) |
| `Talkback` | Talkback during a call (default `true`), needs the Protect key |
| `TokenExpires`, `ProtectKeyExpires` | Expiry dates of the credentials, e.g. `"2027-09-30"` |

Deviations for a single PC: `C:\ProgramData\SIEntryDesk\sientrydesk.local.json`, e.g. `{ "LiveView": false }` or
`{ "Talkback": false }` (the installer writes it with `-NoLiveView` or `-NoTalk`).

The pins only change when the console gets a new certificate. Then the icon is red, and the log names the received
fingerprint. First check whether the certificate was really renewed, then adjust the pin.

## Step 2: install
Put `sientrydesk.json` next to `install.ps1`, then in the unpacked folder, PowerShell **as administrator**:
```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens
```
`-SetTokens` asks for two values without echo:
1. the Access token
2. the Protect key, empty = no live view

The script:
1. checks `sientrydesk.json` (valid JSON, `Host` and `AccessPin` present, `Language` known)
2. stops running instances of service and app
3. copies the program and the admin scripts to `C:\Program Files\SIEntryDesk`
4. sets up the service under its own account `NT SERVICE\SIEntryDesk`, with restart after a crash
5. creates `C:\ProgramData\SIEntryDesk`, readable only by SYSTEM, administrators and the service
6. takes over `sientrydesk.json` there
7. stores the credentials (with `-SetTokens`) or keeps existing ones
8. registers the autostart for all users, blocks inbound connections of the app in the firewall (rule
   "SI EntryDesk: no inbound access"), allows the service UDP 47801 from the local subnet (rule "SI EntryDesk:
   coordination between PCs"), creates the Start menu entry and starts service and app

| Case | Command |
|---|---|
| First installation | `powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens` |
| Update, credentials stay | `powershell -ExecutionPolicy Bypass -File .\install.ps1` |
| Live view without ringing off on this PC | additionally `-NoLiveView` |
| Talkback off on this PC | additionally `-NoTalk` |
| Only renew the credentials | `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"` |
| Remove | `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\uninstall.ps1"`, with `-KeepData` configuration, credentials and logs stay |

Afterwards: icon at the bottom right green, see [operation.md](operation.md#testing-after-the-installation).

## Where the credentials are stored
Nothing is derived from the tokens for storage, the tokens themselves are stored, encrypted:

| | |
|---|---|
| **File** | `C:\ProgramData\SIEntryDesk\secrets.dat` |
| **Encryption** | Windows DPAPI with the key of the computer and a fixed additional value of the app. Can be decrypted only on this PC, a copy of the file is worthless |
| **Access** | The folder is readable only by SYSTEM, administrators and the service `NT SERVICE\SIEntryDesk`, standard users cannot get at it |
| **Who uses them** | Only the service. The tray app knows no tokens, it asks the service via a local connection, e.g. to open |

**Not in the Windows Credential Manager,** because that is per user. The service runs under its own account and needs
the credentials regardless of who is signed in.

**Limit:** A local administrator can decrypt the file. Therefore no local admin rights for users.

Storing always goes through the service (`SIEntryDesk.Service.exe set-secrets`), whether with `install.ps1 -SetTokens`,
`set-tokens.ps1` or via Intune with `set-tokens-intune.ps1`.

## Check and help
After the installation: `SIEntryDesk.exe --check` or right click on the icon → "Check …", see
[operation.md](operation.md#troubleshooting). `SIEntryDesk.exe --help` and `SIEntryDesk.Service.exe --help` show all
calls.
