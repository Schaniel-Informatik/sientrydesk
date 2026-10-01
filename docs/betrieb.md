# Einrichtung und Betrieb

Für Admins, die SI EntryDesk einrichten, verteilen und jährlich die Zugänge erneuern. Anlagenspezifisches
(Adressen, Namen, wo die Zugänge in welchem Passwort-Tresor liegen) gehört ins Betriebshandbuch der Anlage, nicht hierher.

## Bausteine
| Baustein | Wo | Aufgabe |
|---|---|---|
| Dienst `SIEntryDesk` | `C:\Program Files\SIEntryDesk\Service` | Hält die Zugänge, verbindet sich mit Access und Protect, entscheidet über das Öffnen, gibt das Livebild weiter. Läuft unter `NT SERVICE\SIEntryDesk` |
| Tray-App `SIEntryDesk.exe` | `C:\Program Files\SIEntryDesk\App` | Klingelfenster, Livebild, Knopf „Öffnen“. Kennt keine Zugänge |
| Anlagen-Konfiguration | `C:\ProgramData\SIEntryDesk\sientrydesk.json` | Konsole, Zertifikats-Pins, Türen, Ablaufdaten. Keine Geheimnisse |
| Zugänge | `C:\ProgramData\SIEntryDesk\secrets.dat` | Access-Token und Protect-Schlüssel, mit DPAPI verschlüsselt. Nur SYSTEM, Administratoren und der Dienst |
| Protokoll | `C:\ProgramData\SIEntryDesk\logs` | Klingeln, Öffnen, Livebild-Abrufe mit Benutzer. Keine Zugänge, keine Stream-Adressen |

## Zugänge
| # | Wo anlegen | Name (Vorschlag) | Rechte | Wofür |
|---|---|---|---|---|
| 1 | Access → Einstellungen → Allgemein → **API Token** | `SIEntryDesk` | **Gerät = Anzeigen**, **Standorte = Bearbeiten**, alles andere **Keinen** | Klingel-Ereignisse und Öffnen |
| 2 | UniFi OS → Einstellungen → Control Plane → **Integrations** | `SIEntryDesk Livebild` | Rechte des Kontos, unter dem er angelegt wird | Livebild und Kameranamen |

- **Gültigkeit 1 Jahr.** Das Ablaufdatum gehört als `TokenExpires` und `ProtectKeyExpires` in die sientrydesk.json.
  Ab 14 Tagen vorher wird das Symbol orange, und jede App meldet einmal pro Tag, bis wann erneuert werden muss.
- **Der Access-Dialog gibt für mehrere Bereiche „Bearbeiten“ vor.** Alles ausser *Gerät* und *Standorte* auf
  **Keinen** stellen. *Standorte = Bearbeiten* erlaubt, **alle Türen** zu öffnen, dauerhaft offen zu halten und
  Lockdown oder Evakuierung auszulösen. Einschränken lässt sich das bei Access nicht.
- **Protect-Schlüssel:** Er hat die Protect-Rechte des anlegenden Kontos. Unter einem Admin-Konto darf er in
  Protect alles. Sicherer ist ein eigener lokaler Benutzer mit einer Rolle nur zum Ansehen.
- **Nur ein Access-Token.** Getrennte Tokens zum Mithören und Öffnen sind möglich (`set-tokens.ps1` fragt nach
  einem optionalen zweiten), bringen aber keine Sicherheit, weil beide auf demselben PC liegen.
- **Aufbewahrung:** nur im Passwort-Manager, nie in Dateien, Tickets oder Chats. Gelangt ein Wert doch dorthin,
  den Zugang löschen und neu anlegen.

## Voraussetzungen bei UniFi
- **UniFi Access** mit Developer API (Version 1.20.11 oder neuer, nicht mit Identity Enterprise).
- **Türstationen** (z. B. G6 Entry, G6 Pro Entry) in Protect, in Access einer Tür mit UA Hub Door zugeordnet.
- **RTSPS-Stream pro Türstation** in Protect (Kamera → Einstellungen → Erweitert, Qualität „medium“ genügt).
  Die Stream-Adresse wirkt wie ein Zugangsschlüssel zum Kamerabild: nicht weitergeben, nicht protokollieren.
- **Netz:** Die PCs erreichen die Konsole auf TCP 12445 (Access), 443 (Protect) und 7441 (Stream). Die App selbst
  braucht keine eingehenden Verbindungen, der Installer sperrt sie in der Windows-Firewall.

## Einrichtungsassistent
Der einfachste Weg zur Konfiguration. Auf einem Windows-PC im Firmennetz oder über VPN, aus dem entpackten Paket:
```powershell
.\App\SIEntryDesk.exe --setup
```
Er prüft Schritt für Schritt:
1. Erreichbarkeit der Konsole (Ports 12445, 443, 7441) und zeigt die Zertifikats-Fingerabdrücke, die du als Pins bestätigst.
2. Den Access-Token: die nötigen Rechte, Warnung bei überflüssigen. Das Recht zum Öffnen prüft er an einer Tür, die es nicht gibt.
3. Den Protect-Schlüssel: Version und Kameras.
4. Türen und Kameras: Er schlägt die Zuordnung über die Namen vor und zeigt, ob der RTSPS-Stream vorhanden ist. Auf
   Bestätigung legt er ihn an. „Livebild testen“ zeigt das Bild über denselben geprüften Weg wie die App.
5. Einstellungen: Livebild ohne Klingeln, Dauer, Ablaufdaten.
6. Er speichert die sientrydesk.json.

Tokens verwendet der Assistent nur zum Prüfen, er speichert sie nicht.

**Bestehende Konfiguration prüfen:** oben „Installierte Konfiguration laden“ (auf einem PC mit SI EntryDesk, nur als
Administrator lesbar) oder „Andere sientrydesk.json öffnen …“, oder direkt:
```powershell
.\App\SIEntryDesk.exe --setup C:\Pfad\zur\sientrydesk.json
```
Der Assistent übernimmt die Werte und prüft sofort die Konsole. Er zeigt, ob die Zertifikate noch zu den Pins passen
(dann ist die Bestätigung schon gesetzt) und wie lange die Zugänge gültig sind. Mit Access-Token und Protect-Schlüssel
vergleicht er auch Türen und Kameras mit der Datei: fehlende Türen, Kameras, die es nicht mehr gibt, Türen ohne Kamera.
Speichern ist nur nötig, wenn etwas geändert wurde.

`SIEntryDesk.exe --help` und `SIEntryDesk.Service.exe --help` zeigen alle Aufrufe.

## Konfiguration: sientrydesk.json
Erstellt mit dem Einrichtungsassistenten oder von Hand aus `deploy/sientrydesk.example.json`.
Kommentare nur auf eigenen Zeilen.

| Feld | Bedeutung |
|---|---|
| `Host` | Name oder IP der Konsole |
| `AccessPin`, `ProtectPin` | SHA-256-Fingerabdrücke der Zertifikate auf Port 12445 und 443. Anzeigen mit `SIEntryDesk.Service.exe show-pins <Host>`, **nur im Firmennetz oder über VPN** |
| `StreamPin` | nur nötig, wenn Port 7441 ein anderes Zertifikat hat als 443 |
| `Doors` | Türen, die dieser PC anzeigt (Tür-ID oder Name). Leer = alle |
| `DoorCameras` | Tür-ID → Protect-Kamera-ID. Ohne Eintrag lernt der Dienst die Kamera beim ersten Klingeln |
| `LiveView`, `LiveViewSeconds` | Livebild ohne Klingeln im Menü, Dauer 15–600 s (Standard 60) |
| `TokenExpires`, `ProtectKeyExpires` | Ablaufdaten der Zugänge, z. B. `"2027-09-30"` |

Abweichungen für einen einzelnen PC: `C:\ProgramData\SIEntryDesk\sientrydesk.local.json`, z. B. `{ "LiveView": false }`
(der Installer schreibt sie mit `-NoLiveView`).

Die Pins ändern sich nur, wenn die Konsole ein neues Zertifikat bekommt. Dann ist das Symbol rot, und das Protokoll
nennt den erhaltenen Fingerabdruck. Erst prüfen, ob das Zertifikat wirklich erneuert wurde, dann den Pin anpassen.

## Installieren und aktualisieren
Paket entpacken, `sientrydesk.json` daneben legen, PowerShell als Administrator:

| Fall | Befehl |
|---|---|
| Erstinstallation von Hand | `powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens` |
| Update, Zugänge bleiben | `powershell -ExecutionPolicy Bypass -File .\install.ps1` |
| Livebild ohne Klingeln auf diesem PC aus | zusätzlich `-NoLiveView` |
| Entfernen | `powershell -ExecutionPolicy Bypass -File .\uninstall.ps1` (mit `-KeepData` bleiben Konfiguration und Zugänge) |

**Intune:** zwei Win32-Apps an eine Gerätegruppe, aus zwei getrennten ZIP-Dateien: Programm
(`SIEntryDesk-<Version>-win-x64.zip`) und Zugänge (`SIEntryDesk-Zugaenge-<Version>.zip`). Schritt für Schritt in
[intune.md](intune.md).

## Zugänge jährlich erneuern
Ohne Unterbruch, weil der alte Zugang bis zum Schluss gültig bleibt:
1. Neuen Access-Token und neuen Protect-Schlüssel anlegen (Rechte wie oben, 1 Jahr) und im Passwort-Manager ablegen.
2. Auf jedem PC: `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"`.
   Den zweiten Token leer lassen.
3. In der sientrydesk.json die neuen Ablaufdaten eintragen und mit `install.ps1` verteilen.
4. Prüfen: Symbol grün, Livebild aus dem Menü, einmal klingeln.
5. Erst dann den alten Token und den alten Schlüssel löschen.
6. Im Betriebshandbuch der Anlage vermerken.

## Störungen
| Symbol | Bedeutung | Was tun |
|---|---|---|
| grün | bereit | – |
| blau | Klingel auf diesem PC pausiert | über das Menü fortsetzen oder abwarten |
| grau | Konsole nicht erreichbar | ausserhalb des Firmennetzes normal. Im Büro: Netz oder VPN, Ports prüfen |
| orange | Klingeln geht, Livebild gestört oder Zugänge laufen bald ab | Text im Menü lesen. Protect-Schlüssel prüfen oder Zugänge erneuern |
| rot | **dieser PC klingelt nicht** | Text im Menü und Protokoll: Dienst läuft nicht, Token abgelehnt, Zertifikat passt nicht zum Pin |

## Sicherheit und Grenzen
- Wer auf einem PC **lokaler Administrator** ist, kann die Zugänge aus `secrets.dat` auslesen und damit alle Türen
  öffnen. Standardbenutzer können das nicht. Deshalb: keine lokalen Adminrechte für Benutzer, BitLocker, LAPS.
- Die App öffnet nur während eines Rufs oder bis 10 s danach (nach „Besucher hat abgebrochen“ oder „Niemand hat
  abgenommen“). Das erzwingt der Dienst. Wer einen Token ausserhalb der App verwendet, ist daran nicht gebunden.
- Jede Öffnung steht im Access-Protokoll mit Benutzer und PC, jeder Livebild-Abruf im Protokoll des Dienstes.
- Das Livebild läuft nur über die geprüfte TLS-Verbindung des Dienstes. Die App bekommt eine Einmal-Adresse auf
  127.0.0.1, die mit dem Ruf bzw. nach der eingestellten Dauer endet.
