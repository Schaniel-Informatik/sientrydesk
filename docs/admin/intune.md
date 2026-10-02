# Verteilung mit Intune

Voraussetzungen, Zugänge und `sientrydesk.json` wie in [installation.md](installation.md). Bedienung und Störungen in
[betrieb.md](betrieb.md).

Zwei Win32-Apps an dieselbe **Gerätegruppe** (nicht Benutzer), damit die Zugänge nur auf den vorgesehenen Geräten
liegen und die App für jeden Benutzer dieser Geräte startet:

| Intune-App | Aus der Datei | Dazu kommt | Wird erneuert |
|---|---|---|---|
| **SI EntryDesk** | `SIEntryDesk-<Version>-win-x64.zip` | `sientrydesk.json` der Anlage | bei jedem Software-Update |
| **SI EntryDesk Zugänge** | `SIEntryDesk-Zugaenge-<Version>.zip` | `tokens.txt` mit den Zugängen | jährlich mit den Zugängen |

Zwei ZIP-Dateien, zwei Ordner, je ein Vorbereitungsskript (`prepare-intune.ps1`). So stehen die Zugänge nie im
Programmpaket, und ein Software-Update berührt sie nicht.

## Vorbereitung
- **Microsoft Win32 Content Prep Tool** (`IntuneWinAppUtil.exe`) auf dem Windows-PC, auf dem die Pakete entstehen,
  neben die entpackten Ordner legen. `prepare-intune.ps1` findet es dort, im Suchpfad oder unter Downloads.
- **Gerätegruppe** in Entra ID, z. B. „SIEntryDesk-Geräte“. Zum Testen zuerst eine Gruppe mit einem einzigen Gerät.
- `sientrydesk.json` der Anlage, am einfachsten mit dem Einrichtungsassistenten (`SIEntryDesk.exe --setup`).
  Eine bestehende vor dem Packen prüfen: `SIEntryDesk.exe --setup <Pfad>\sientrydesk.json`, siehe
  [installation.md](installation.md#schritt-1-sientrydeskjson-erstellen).

## App 1: SI EntryDesk
1. `SIEntryDesk-<Version>-win-x64.zip` entpacken, `sientrydesk.json` in den Ordner neben `install.ps1` legen.
2. Im entpackten Ordner:
   ```
   powershell -ExecutionPolicy Bypass -File .\prepare-intune.ps1
   ```
   Es prüft den Ordner (Programmdateien vollständig, `sientrydesk.json` gültig, keine `tokens.txt`, nichts
   Überzähliges), erzeugt `detect.ps1` und legt neben dem Paketordner `Intune-SIEntryDesk-<Version>` an, darin
   `SIEntryDesk-<Version>.intunewin` und `detect.ps1`. Am Schluss zeigt es die Einstellungen für Intune.
3. Intune → Apps → Windows → Erstellen → **Windows-App (Win32)**, die `.intunewin` hochladen:

| Einstellung | Wert |
|---|---|
| App-Version | die Version, z. B. `0.5.2` (nur Beschriftung) |
| Installationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1` |
| Deinstallationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1` |
| Installationsverhalten | **System** |
| Neustart des Geräts | Keine bestimmte Aktion |
| Anforderungen | 64 Bit, Windows 11 |
| Erkennungsregel | **Benutzerdefiniertes Skript**: `detect.ps1` aus `Intune-SIEntryDesk-<Version>`. Als 32-Bit-Prozess ausführen: **Nein**. Signaturprüfung erzwingen: **Nein** |
| Zuweisung | **Erforderlich** → Gerätegruppe |

`detect.ps1` meldet „installiert“, wenn der Dienst vorhanden ist, `SIEntryDesk.exe` mindestens diese Version hat und
die installierte `sientrydesk.json` genau der aus dem Paket entspricht. So verteilt Intune auch eine geänderte
Konfiguration bei gleicher Version neu, z. B. neue Ablaufdaten. `install.ps1` legt `detect.ps1` zusammen mit
`uninstall.ps1` und `set-tokens.ps1` auch nach `C:\Program Files\SIEntryDesk`. Dort als Administrator ausgeführt,
zeigt es am Gerät dasselbe wie Intune: eine Zeile „installiert“ oder nichts.

Das Installationsskript fragt nichts, behält vorhandene Zugänge und startet die Tray-App nach einem Update wieder in
allen angemeldeten Sitzungen. Die Benutzer merken vom Update höchstens ein kurzes Verschwinden des Symbols.

Intune startet den Installationsbefehl als 32-Bit-Prozess. Die Skripte wechseln selbst in die 64-Bit-PowerShell,
der Befehl bleibt deshalb einfach `powershell.exe …`.

## App 2: SI EntryDesk Zugänge
1. `SIEntryDesk-Zugaenge-<Version>.zip` entpacken, am besten auf einem vertrauenswürdigen Admin-PC.
2. Im entpackten Ordner `tokens.example.txt` als `tokens.txt` kopieren und die Werte direkt hinter `access=` und
   `protect=` einsetzen, ohne Leerzeichen, Anführungszeichen oder `<>`. Die Zeilen mit `#` dürfen bleiben:
   ```
   access=<Access-Token>
   protect=<Protect-Schlüssel>
   ```
3. Im selben Ordner, mit der Kennung der Zugänge (Jahr-Monat):
   ```
   powershell -ExecutionPolicy Bypass -File .\prepare-intune.ps1 -Label 2026-09
   ```
   Es prüft `tokens.txt` (Fehler mit Zeilennummer, nie mit Inhalt), erstellt neben dem Ordner
   `Intune-SIEntryDesk-Zugaenge-2026-09` mit der `.intunewin`, **löscht `tokens.txt`** und zeigt die Einstellungen.
4. In Intune als Win32-App hinzufügen, Name z. B. „SI EntryDesk Zugänge 2026-09“:

| Einstellung | Wert |
|---|---|
| Installationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label 2026-09` |
| Deinstallationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove` |
| Installationsverhalten | **System** |
| Anforderungen | 64 Bit, Windows 11 |
| Erkennungsregel | Manuell, Registrierung: `HKEY_LOCAL_MACHINE\SOFTWARE\SIEntryDesk`, Wertname `TokenLabel`, **Zeichenfolgenvergleich**, *Ist gleich* `2026-09`, 32-Bit-App auf 64-Bit-Clients: **Nein** |
| Abhängigkeit | **SI EntryDesk**, automatisch installieren |
| Zuweisung | **Erforderlich** → dieselbe Gerätegruppe |

**Sicherheit:** Intune speichert den Paketinhalt verschlüsselt. Auf dem Gerät liegt `tokens.txt` nur während der
Installation im Zwischenspeicher der Intune-Erweiterung. Wer in Intune Apps verwalten darf, kann die Zugänge über
ein eigenes Paket austauschen, aber nicht auslesen. Die Kennung (`2026-09`) ist kein Geheimnis.

## Software-Update
Neues `SIEntryDesk-<Version>-win-x64.zip` entpacken, `sientrydesk.json` dazulegen, `prepare-intune.ps1` ausführen.
In Intune die App **SI EntryDesk** bearbeiten: Paketdatei und Erkennungsskript ersetzen, *App-Version* nachführen.
Dasselbe, wenn sich nur die `sientrydesk.json` ändert. *App-Version* ist nur eine Beschriftung in den Berichten,
welche Version ein Gerät wirklich hat, steht auf dem Gerät.

## Zugänge jährlich erneuern
1. Neue Zugänge anlegen ([Rechte wie bei der Installation](installation.md#zugänge-anlegen)). Aus dem aktuellen `SIEntryDesk-Zugaenge-<Version>.zip` wie oben ein
   neues Paket mit `prepare-intune.ps1 -Label 2027-09` erstellen, App „SI EntryDesk Zugänge 2027-09“.
2. In Intune als neue App anlegen, die „SI EntryDesk Zugänge 2026-09“ per **Ablösung** ersetzt, *ohne* Deinstallation.
3. Die neuen Ablaufdaten in der `sientrydesk.json` nachführen und mit der App SI EntryDesk verteilen, siehe
   Software-Update.
4. Wenn alle Geräte die neue Kennung melden: die alten Zugänge in UniFi löschen.

## Kontrolle
- Intune → App → Geräteinstallationsstatus. Der Bericht hinkt nach, oft um eine Stunde und mehr.
- Sofort prüfen lassen statt warten: auf dem Gerät als Administrator
  `Restart-Service IntuneManagementExtension`. Die Intune-Erweiterung prüft dann ihre Apps gleich neu.
- Installierte Version auf dem Gerät:
  `(Get-Item 'C:\Program Files\SIEntryDesk\App\SIEntryDesk.exe').VersionInfo.FileVersion`.
- Auf dem Gerät: `HKLM\SOFTWARE\SIEntryDesk` mit `Version` und `TokenLabel`, Symbol grün, Protokoll unter
  `C:\ProgramData\SIEntryDesk\logs`.
- Installation über Intune: `C:\Windows\Temp\SIEntryDesk-install.log`. Intune selbst:
  `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log`.
- „Nach erfolgreicher Installation nicht erkannt“ (0x87D1041C): Das Skript lief durch, die Erkennung passt nicht.
  `C:\Program Files\SIEntryDesk\detect.ps1` als Administrator ausführen; ohne Ausgabe fehlt etwas.

### Fehlgeschlagene App sofort neu versuchen
Nach einem Fehler versucht die Intune-Erweiterung eine Win32-App erst nach **24 Stunden** wieder (Global
Re-evaluation Schedule). Neustart der Erweiterung oder des Geräts ändert daran nichts, auch nicht ein neues Paket.
Auf einem Testgerät lässt sich der lokale Zustand zurücksetzen, die App wird dabei nicht deinstalliert. Die App-IDs
stehen in Intune in der Adresse der App (`…/appId/<App-ID>`), PowerShell als Administrator:
```powershell
& {
    $appIds = '<App-ID SI EntryDesk>', '<App-ID SI EntryDesk Zugänge>'
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
