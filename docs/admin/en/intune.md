# Distribution with Intune

German version: [../intune.md](../intune.md). Prerequisites, credentials and `sientrydesk.json` as in
[installation.md](installation.md). Operation and troubleshooting in [operation.md](operation.md).

Two Win32 apps assigned to the same **device group** (not users), so that the credentials exist only on the intended
devices and the app starts for every user of these devices:

| Intune app | From the file | Plus | Renewed |
|---|---|---|---|
| **SI EntryDesk** | `SIEntryDesk-<version>-win-x64.zip` | `sientrydesk.json` of the installation | with every software update |
| **SI EntryDesk Tokens** | `SIEntryDesk-Tokens-<version>.zip` (own version, e.g. 1.1) | `tokens.txt` with the credentials | yearly with the credentials |

Two ZIP files, two folders, one preparation script each (`prepare-intune.ps1`). The credentials package has its own
version and changes rarely. A distributed credentials package also fits newer program versions, it is only created
again at the yearly renewal or when the release notes ask for it. This way the credentials are never in the program
package, and a software update does not touch them.

## Preparation
- **Microsoft Win32 Content Prep Tool** (`IntuneWinAppUtil.exe`) on the Windows PC where the packages are created, next
  to the unpacked folders. `prepare-intune.ps1` finds it there, in the path or under Downloads.
- **Device group** in Entra ID, e.g. "SIEntryDesk devices". For testing first a group with a single device.
- `sientrydesk.json` of the installation, easiest with the setup wizard (`SIEntryDesk.exe --setup`). Check an existing
  one before packing: `SIEntryDesk.exe --setup <path>\sientrydesk.json`, see
  [installation.md](installation.md#step-1-create-sientrydeskjson).

## App 1: SI EntryDesk
1. Unpack `SIEntryDesk-<version>-win-x64.zip`, put `sientrydesk.json` into the folder next to `install.ps1`.
2. In the unpacked folder **double-click `prepare-intune.cmd`**. If `sientrydesk.json` is missing or it does not find
   `IntuneWinAppUtil.exe`, it asks with a dialog. It checks the folder (program files complete, `sientrydesk.json`
   valid, no `tokens.txt`, nothing extra), creates `detect.ps1` and creates next to the package folder
   `Intune-SIEntryDesk-<version>` with `SIEntryDesk-<version>.intunewin` and `detect.ps1`. At the end it shows the
   settings for Intune.
3. Intune → Apps → Windows → Create → **Windows app (Win32)**, upload the `.intunewin`:

| Setting | Value |
|---|---|
| App version | the version, e.g. `0.10.0` (label only) |
| Install command | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1` |
| Uninstall command | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1` |
| Install behavior | **System** |
| Device restart behavior | No specific action |
| Requirements | 64-bit, Windows 11 |
| Detection rule | **Custom script**: `detect.ps1` from `Intune-SIEntryDesk-<version>`. Run script as 32-bit process: **No**. Enforce script signature check: **No** |
| Assignment | **Required** → device group |

`detect.ps1` reports "installed" when the service exists, `SIEntryDesk.exe` has at least this version and the installed
`sientrydesk.json` is exactly the one from the package. This way Intune also distributes a changed configuration with
the same version, e.g. new expiry dates. `install.ps1` also puts `detect.ps1` together with `uninstall.ps1` and
`set-tokens.ps1` into `C:\Program Files\SIEntryDesk`. Run there as administrator, it shows on the device the same as
Intune: one line "installed" or nothing.

The install script asks nothing, keeps existing credentials and starts the tray app again in all signed-in sessions
after an update. Users notice the update at most by the icon disappearing briefly.

Intune starts the install command as a 32-bit process. The scripts switch to the 64-bit PowerShell themselves, so the
command stays simply `powershell.exe …`.

## App 2: SI EntryDesk Tokens
1. Unpack `SIEntryDesk-Tokens-<version>.zip`, preferably on a trusted admin PC.
2. In the unpacked folder copy `tokens.example.txt` to `tokens.txt` and put the values right after `access=` and
   `protect=`, without spaces, quotes or `<>`. The lines with `#` may stay:
   ```
   access=<Access token>
   protect=<Protect key>
   ```
3. In the same folder **double-click `prepare-intune.cmd`** and confirm the label of the credentials (year-month). It
   checks `tokens.txt` (errors with line number, never with content), creates next to the folder
   `Intune-SIEntryDesk-Tokens-2026-09` with `SIEntryDesk-Tokens-2026-09.intunewin`, **deletes `tokens.txt`** and shows
   the settings.
4. Add it in Intune as a Win32 app, name e.g. "SI EntryDesk Tokens 2026-09":

| Setting | Value |
|---|---|
| Install command | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label 2026-09` |
| Uninstall command | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove` |
| Install behavior | **System** |
| Requirements | 64-bit, Windows 11 |
| Detection rule | Manual, registry: `HKEY_LOCAL_MACHINE\SOFTWARE\SIEntryDesk`, value name `TokenLabel`, **string comparison**, *equals* `2026-09`, associated with a 32-bit app on 64-bit clients: **No** |
| Dependency | **SI EntryDesk**, install automatically |
| Assignment | **Required** → the same device group |

**Security:** Intune stores the package content encrypted. On the device `tokens.txt` exists only during the
installation in the cache of the Intune extension. Whoever may manage apps in Intune can replace the credentials with a
package of their own, but cannot read them. The label (`2026-09`) is not a secret.

## Software update
**Important: do not create a new program app with supersedence.** The app with the credentials depends via the fixed
ID on exactly one program app. If that is superseded by a new app, Intune no longer installs the credentials: "App
cannot be installed due to a supersedence relationship conflict" (0x87D300DB), the PC stays red without credentials.
If it did happen: in the app with the credentials move the dependency to the new program app, reset the 24-hour block
on affected devices (see below) and delete the old program apps.

Unpack the new `SIEntryDesk-<version>-win-x64.zip`, add `sientrydesk.json`, run `prepare-intune.ps1`. In Intune edit
the app **SI EntryDesk**: replace package file and detection script, update *App version*. The same if only
`sientrydesk.json` changes. *App version* is only a label in the reports, which version a device really has is on the
device.

**When the devices get the update:** Only at the next re-evaluation of the app, and for an installed app it comes up to
**24 hours** after the last one (global re-evaluation schedule). Restarting the Intune extension or the device does not
speed this up. Meanwhile the Company Portal already shows the new *App version* with "Installed"; that is only the label
and the old check. For the users the day of delay is no problem. To update a test device immediately: reset the block
as under [Retry a failed app immediately](#retry-a-failed-app-immediately), only with the app ID of "SI EntryDesk".

## Renew the credentials yearly
1. Create new credentials ([permissions as for the installation](installation.md#create-the-credentials)). From the
   current `SIEntryDesk-Tokens-<version>.zip` create a new package as above with `prepare-intune.ps1 -Label 2027-09`,
   app "SI EntryDesk Tokens 2027-09".
2. Create it in Intune as a new app that replaces the previous app with the credentials by **supersedence**, *without*
   uninstalling.
3. Update the new expiry dates in `sientrydesk.json` and distribute it with the app SI EntryDesk, see software update.
4. When all devices report the new label: delete the old credentials in UniFi.

## Control
- On the device as user: `C:\Program Files\SIEntryDesk\App\SIEntryDesk.exe --check` or right click on the icon →
  "Check …". Shows all prerequisites green, orange or red.
- Intune → app → device install status. The report lags behind, often by an hour and more.
- Let it check immediately instead of waiting: on the device as administrator `Restart-Service IntuneManagementExtension`.
  The Intune extension then checks its apps again right away.
- Installed version on the device: `(Get-Item 'C:\Program Files\SIEntryDesk\App\SIEntryDesk.exe').VersionInfo.FileVersion`.
- On the device: `HKLM\SOFTWARE\SIEntryDesk` with `Version` and `TokenLabel`, icon green, log under
  `C:\ProgramData\SIEntryDesk\logs`.
- `TokenLabel` missing although the credentials were installed: up to 0.10.0 the program update deleted `TokenLabel`
  (and the credentials package up to 1.2 deleted `Version` the other way round). The credentials themselves stay, the
  service stays connected. At the next re-evaluation Intune simply installs the app with the credentials again, after
  that it is detected again. From 0.10.1 on the values of the other package are kept, nothing to do.
- Installation via Intune: `C:\Windows\Temp\SIEntryDesk-install.log`. Intune itself:
  `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log`.
- "Not detected after successful installation" (0x87D1041C): the script ran through, the detection does not match.
  Run `C:\Program Files\SIEntryDesk\detect.ps1` as administrator; without output something is missing.

### Retry a failed app immediately
After a failure, but also after a successful installation, the Intune extension re-evaluates a Win32 app only after
**24 hours** (global re-evaluation schedule). Restarting the extension or the device does not change this, neither does
a new package. The log `AppWorkload.log` then says "Reevaluation interval is not expired". On a test device the local
state can be reset, the app is not uninstalled by this. The app IDs are in the address of the app in Intune
(`…/appId/<app ID>`), PowerShell as administrator:
```powershell
& {
    $appIds = '<app ID SI EntryDesk>', '<app ID of the app with the credentials>'
    $root = 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps'
    foreach ($scope in Get-ChildItem $root -ErrorAction SilentlyContinue) {
        foreach ($appId in $appIds) {
            Get-ChildItem $scope.PSPath | Where-Object { $_.PSChildName -like "$appId*" } | Remove-Item -Recurse -Force
            Get-ChildItem (Join-Path $scope.PSPath 'GRS') -ErrorAction SilentlyContinue |
                Where-Object { (Get-Item $_.PSPath).Property | Where-Object { $_ -like "$appId*" } } |
                Remove-Item -Recurse -Force
        }
    }
    Restart-Service IntuneManagementExtension
}
```
