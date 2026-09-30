# Verteilung mit Intune

Zwei Win32-Apps an dieselbe **Gerätegruppe** (nicht Benutzer), damit die Zugänge nur auf den vorgesehenen Geräten
liegen und die App für jeden Benutzer dieser Geräte startet:

| App | Inhalt | Wird erneuert |
|---|---|---|
| **SI EntryDesk** | Programm, Installationsskripte, `sientrydesk.json` der Anlage | bei jedem Software-Update |
| **SI EntryDesk Zugänge** | `set-tokens-intune.ps1` und `tokens.txt` | jährlich mit den Zugängen |

So stehen die Zugänge nie im Programmpaket, und ein Software-Update berührt sie nicht.

## Vorbereitung
- **Microsoft Win32 Content Prep Tool** (`IntuneWinAppUtil.exe`) auf einem Windows-PC.
- **Gerätegruppe** in Entra ID, z. B. „SIEntryDesk-Geräte“. Zum Testen zuerst eine Gruppe mit einem einzigen Gerät.
- `sientrydesk.json` der Anlage, am einfachsten mit dem Einrichtungsassistenten (`SIEntryDesk.exe --setup`).

## App 1: SI EntryDesk
1. Paket entpacken, `sientrydesk.json` in den Ordner neben `install.ps1` legen.
2. Paket erstellen:
   ```
   IntuneWinAppUtil.exe -c <Paketordner> -s install.ps1 -o <Ausgabeordner> -q
   ```
3. Intune → Apps → Windows → Hinzufügen → **Windows-App (Win32)**, die `.intunewin` hochladen.

| Einstellung | Wert |
|---|---|
| Installationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1` |
| Deinstallationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1` |
| Installationsverhalten | **System** |
| Neustart des Geräts | Keine bestimmte Aktion |
| Anforderungen | Windows 11, 64 Bit |
| Erkennungsregel | Datei: `C:\Program Files\SIEntryDesk\App`, Datei `SIEntryDesk.exe`, **Version**, *größer oder gleich*, z. B. `0.4.2.0` |
| Zuweisung | **Erforderlich** → Gerätegruppe |

Das Installationsskript fragt nichts, behält vorhandene Zugänge und startet die Tray-App nach einem Update wieder in
allen angemeldeten Sitzungen. Die Benutzer merken vom Update höchstens ein kurzes Verschwinden des Symbols.

## App 2: SI EntryDesk Zugänge
1. Einen **eigenen** Ordner anlegen, am besten auf einem vertrauenswürdigen Admin-PC, mit:
   - `set-tokens-intune.ps1` (aus `intune-zugaenge` im Paket)
   - `tokens.txt` nach `tokens.example.txt`: Zeile 1 Access-Token, Zeile 2 leer, Zeile 3 Protect-Schlüssel
2. Paket erstellen, dann **`tokens.txt` sofort löschen**:
   ```
   IntuneWinAppUtil.exe -c <Ordner> -s set-tokens-intune.ps1 -o <Ausgabeordner> -q
   ```
3. In Intune als Win32-App hinzufügen, Name z. B. „SI EntryDesk Zugänge 2026-09“:

| Einstellung | Wert |
|---|---|
| Installationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label 2026-09` |
| Deinstallationsbefehl | `powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove` |
| Installationsverhalten | **System** |
| Erkennungsregel | Registrierung: `HKEY_LOCAL_MACHINE\SOFTWARE\SIEntryDesk`, Wert `TokenLabel`, **Zeichenfolgenvergleich**, *gleich* `2026-09` |
| Abhängigkeit | **SI EntryDesk**, automatisch installieren |
| Zuweisung | **Erforderlich** → dieselbe Gerätegruppe |

**Sicherheit:** Intune speichert den Paketinhalt verschlüsselt. Auf dem Gerät liegt `tokens.txt` nur während der
Installation im Zwischenspeicher der Intune-Erweiterung. Wer in Intune Apps verwalten darf, kann die Zugänge über
ein eigenes Paket austauschen, aber nicht auslesen. Die Kennung (`2026-09`) ist kein Geheimnis.

## Software-Update
Neue Version entpacken, `sientrydesk.json` dazulegen, `.intunewin` erstellen. In Intune die App **SI EntryDesk**
bearbeiten: Paket ersetzen und die Version in der Erkennungsregel erhöhen. Alternativ eine neue App anlegen, die die
alte per **Ablösung** ersetzt, *ohne* Deinstallation der alten.

## Zugänge jährlich erneuern
1. Neue Zugänge anlegen (siehe `betrieb.md`), neues Paket „SI EntryDesk Zugänge 2027-09“ mit `-Label 2027-09`.
2. In Intune als neue App anlegen, die „SI EntryDesk Zugänge 2026-09“ per **Ablösung** ersetzt, *ohne* Deinstallation.
3. Die neuen Ablaufdaten in der `sientrydesk.json` nachführen und mit der App SI EntryDesk verteilen.
4. Wenn alle Geräte die neue Kennung melden: die alten Zugänge in UniFi löschen.

## Kontrolle
- Intune → App → Geräteinstallationsstatus.
- Auf dem Gerät: `HKLM\SOFTWARE\SIEntryDesk` mit `Version` und `TokenLabel`, Symbol grün, Protokoll unter
  `C:\ProgramData\SIEntryDesk\logs`.
