# Installation

Für Admins. Es gibt zwei Wege: **von Hand** auf einzelnen PCs (diese Anleitung) oder **per Intune** an eine
Gerätegruppe ([intune.md](intune.md)). Beide brauchen dieselben Voraussetzungen, dieselben Zugänge und dieselbe
`sientrydesk.json`. Bedienung, Erneuerung und Störungen stehen in [betrieb.md](betrieb.md).

Anlagenspezifisches (Adressen, Namen, wo die Zugänge in welchem Passwort-Tresor liegen) gehört ins Betriebshandbuch
der Anlage, nicht hierher.

## Was wo liegt
| Was | Wo | Inhalt |
|---|---|---|
| Dienst `SIEntryDesk` | `C:\Program Files\SIEntryDesk\Service` | Hält die Zugänge, verbindet sich mit Access und Protect, entscheidet über das Öffnen, gibt das Livebild weiter. Läuft unter `NT SERVICE\SIEntryDesk` |
| Tray-App `SIEntryDesk.exe` | `C:\Program Files\SIEntryDesk\App` | Klingelfenster, Livebild, Knopf „Öffnen“. Kennt keine Zugänge |
| Skripte für Admins | `C:\Program Files\SIEntryDesk` | `set-tokens.ps1` (Zugänge erneuern), `uninstall.ps1`, bei Intune `detect.ps1` |
| Anlagen-Konfiguration | `C:\ProgramData\SIEntryDesk\sientrydesk.json` | Konsole, Zertifikats-Pins, Türen, Ablaufdaten. Keine Geheimnisse |
| Zugänge | `C:\ProgramData\SIEntryDesk\secrets.dat` | Access-Token und Protect-Schlüssel, verschlüsselt, siehe [unten](#wo-die-zugänge-gespeichert-sind) |
| Protokoll | `C:\ProgramData\SIEntryDesk\logs` | Klingeln, Öffnen, Livebild-Abrufe mit Benutzer. Keine Zugänge, keine Stream-Adressen |
| Version, Kennung der Zugänge | `HKLM\SOFTWARE\SIEntryDesk` | `Version`, bei Intune `TokenLabel`. Keine Geheimnisse |

## Voraussetzungen
- **PC:** Windows 11, 64 Bit. Für die Installation lokaler Administrator. Für Gegensprechen ein Mikrofon, am besten
  ein Headset, und in Windows unter Datenschutz → Mikrofon „Desktop-Apps den Zugriff erlauben“ (Standard).
- **UniFi-OS-Konsole** mit UniFi Access und UniFi Protect.
- **UniFi Access** mit Developer API (Version 1.20.11 oder neuer, nicht mit Identity Enterprise): Klingel-Ereignisse
  und Öffnen.
- **UniFi Protect** mit Integration API (API-Schlüssel unter UniFi OS → Einstellungen → Control Plane →
  Integrations): Livebild und Kameranamen. Ohne geht alles ausser dem Livebild.
- **Türstationen** (z. B. G6 Entry, G6 Pro Entry) in Protect, in Access einer Tür mit UA Hub Door zugeordnet.
- **Netz:** Die PCs erreichen die Konsole auf TCP 12445 (Access), 443 (Protect) und 7441 (Stream). Für
  Gegensprechen zusätzlich **UDP 7004 direkt zu den Türstationen**, nicht zur Konsole: Protect nennt die Türstation
  als Ziel für den Ton. Dieser Ton ist unverschlüsselt, so hat Ubiquiti die Schnittstelle gebaut. Die App braucht
  keine eingehenden Verbindungen, der Installer sperrt sie in der Windows-Firewall.

## Wer öffnen darf: der PC, nicht die Person
**Die Personen, Rollen und Türberechtigungen aus UniFi Access gelten für SI EntryDesk nicht.** Die offizielle Access
Developer API kennt keine Anmeldung einzelner Benutzer. Sie arbeitet mit einem API-Token, und der gilt für die ganze
Installation: *Standorte = Bearbeiten* erlaubt, **jede Tür** zu öffnen. Einschränken lässt sich das nicht.

Das heisst:
- **Jeder, der an einem PC mit SI EntryDesk angemeldet ist, kann während eines Rufs öffnen**, egal ob er in Access
  für diese Tür berechtigt ist oder als Empfänger von Klingelrufen (Door Attendant, Doorbell Call Receiver) eingetragen
  ist. Die Berechtigung ergibt sich daraus, **auf welchen PCs** die App installiert ist. Das ist die eigentliche
  Entscheidung, bei Intune über die Gerätegruppe.
- `Doors` in der sientrydesk.json legt fest, welche Türen ein PC anzeigt und öffnet. Das erzwingt der Dienst, der
  Token selbst könnte trotzdem alle.
- Im Access-Protokoll steht bei jeder Öffnung „<Windows-Benutzer> via SI EntryDesk (<PC>)“. Diesen Namen meldet
  SI EntryDesk, Access prüft ihn nicht.
- Wer den Token selbst hat, etwa ein lokaler Administrator des PCs, kann jederzeit jede Tür öffnen, ohne Ruf.

Sobald Ubiquiti eine API mit Berechtigungen pro Benutzer anbietet, kann SI EntryDesk sie übernehmen. Bis dahin ist
die Auswahl der PCs die Zugriffskontrolle.

## Vorbereitung in UniFi
Einmal pro Anlage, vor der ersten Installation.

### Zugänge anlegen
| # | Wo anlegen | Name (Vorschlag) | Rechte | Wofür |
|---|---|---|---|---|
| 1 | Access → Einstellungen → Allgemein → **API Token** | `SIEntryDesk` | **Gerät = Anzeigen**, **Standorte = Bearbeiten**, alles andere **Keinen** | Klingel-Ereignisse und Öffnen |
| 2 | UniFi OS → Einstellungen → Control Plane → **Integrations** | `SIEntryDesk Livebild` | Rechte des Kontos, unter dem er angelegt wird | Livebild und Kameranamen |

- **Ein Access-Token für alles.** Er empfängt die Klingel-Ereignisse und öffnet die Tür.
- **Gültigkeit 1 Jahr.** Das Ablaufdatum gehört als `TokenExpires` und `ProtectKeyExpires` in die sientrydesk.json.
  Ab 14 Tagen vorher wird das Symbol orange, und jede App meldet einmal pro Tag, bis wann erneuert werden muss.
- **Der Access-Dialog gibt für mehrere Bereiche „Bearbeiten“ vor.** Alles ausser *Gerät* und *Standorte* auf
  **Keinen** stellen. *Standorte = Bearbeiten* erlaubt, **alle Türen** zu öffnen, dauerhaft offen zu halten und
  Lockdown oder Evakuierung auszulösen. Einschränken lässt sich das bei Access nicht.
- **Protect-Schlüssel:** Er hat die Protect-Rechte des anlegenden Kontos. Unter einem Admin-Konto darf er in
  Protect alles. Sicherer ist ein eigener lokaler Benutzer mit einer Rolle nur zum Ansehen.
- **Aufbewahrung:** nur im Passwort-Manager, nie in Dateien, Tickets oder Chats. Gelangt ein Wert doch dorthin,
  den Zugang löschen und neu anlegen.

### RTSPS-Stream pro Türstation
Das Livebild holt der Dienst als RTSPS-Stream von der Türstation. Protect liefert ihn erst, wenn er für die Kamera
eingeschaltet ist, und zwar für **jede Türstation einzeln**:
- **In Protect:** Kamera der Türstation → Einstellungen → Erweitert → RTSP, Qualität **Mittel** einschalten. Höhere
  Qualität bringt im kleinen Fenster nichts und kostet Bandbreite.
- **Oder im Einrichtungsassistenten** ([Schritt 1](#mit-dem-einrichtungsassistenten)): Er zeigt pro Tür, ob der
  Stream vorhanden ist, und legt ihn auf Knopfdruck an.

Die Stream-Adresse enthält einen geheimen Teil und wirkt wie ein Zugangsschlüssel zum Kamerabild: nicht weitergeben,
nicht in Dokumente, Tickets oder Chats. SI EntryDesk gibt sie nicht an die Benutzer weiter und schreibt sie nicht ins
Protokoll. Ist sie doch nach aussen gelangt: Stream in Protect löschen und neu anlegen, dann gilt eine neue Adresse.

## Die Pakete
Der Build liefert zwei ZIP-Dateien:

| Datei | Inhalt | Wofür |
|---|---|---|
| `SIEntryDesk-<Version>-win-x64.zip` | `App\`, `Service\`, `install.ps1`, `uninstall.ps1`, `set-tokens.ps1`, `prepare-intune.ps1`, `sientrydesk.example.json`, `Doku\` | Installation von Hand und Intune-App „SI EntryDesk“ |
| `SIEntryDesk-Zugaenge-<Version>.zip` | `set-tokens-intune.ps1`, `prepare-intune.ps1`, `tokens.example.txt` | nur für die Intune-App „SI EntryDesk Zugänge“ |

Für die Installation von Hand braucht es nur das Programmpaket.

PowerShell-Skripte aus dem Internet blockiert Windows. Deshalb jedes Skript so starten:
`powershell -ExecutionPolicy Bypass -File .\<skript>.ps1`. Das gilt nur für diesen Aufruf und ändert keine
Einstellung am PC.

## Schritt 1: sientrydesk.json erstellen
**Die Datei muss vor der Installation fertig sein.** `install.ps1` fragt keine Einstellungen ab und bricht ohne sie
mit „Konfiguration nicht gefunden“ ab. Sie enthält keine Geheimnisse und gilt für alle PCs einer Anlage.

### Mit dem Einrichtungsassistenten
Der einfachste Weg. Auf einem Windows-PC im Firmennetz oder über VPN, direkt aus dem entpackten Paket, ohne
Installation:
```powershell
.\App\SIEntryDesk.exe --setup
```
Er prüft Schritt für Schritt:
1. Erreichbarkeit der Konsole (Ports 12445, 443, 7441) und zeigt die Zertifikats-Fingerabdrücke, die du als Pins
   bestätigst.
2. Den Access-Token: die nötigen Rechte, Warnung bei überflüssigen. Das Recht zum Öffnen prüft er an einer Tür, die
   es nicht gibt.
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
Der Assistent übernimmt die Werte und prüft sofort die Konsole: ob die Fingerabdrücke noch zu den Pins passen und wie
lange die Zugänge gültig sind. Mit Access-Token und Protect-Schlüssel vergleicht er auch Türen und Kameras mit der
Datei. Speichern ist nur nötig, wenn etwas geändert wurde.

### Von Hand
Aus der Vorlage `sientrydesk.example.json` im Paket. Kommentare nur auf eigenen Zeilen.

| Feld | Bedeutung |
|---|---|
| `Host` | Name oder IP der Konsole |
| `AccessPin`, `ProtectPin` | SHA-256-Fingerabdrücke der Zertifikate auf Port 12445 und 443. Anzeigen mit `.\Service\SIEntryDesk.Service.exe show-pins <Host>`, **nur im Firmennetz oder über VPN** |
| `StreamPin` | nur nötig, wenn Port 7441 ein anderes Zertifikat hat als 443 |
| `Doors` | Türen, die dieser PC anzeigt (Tür-ID oder Name). Leer = alle |
| `DoorCameras` | Tür-ID → Protect-Kamera-ID. Ohne Eintrag lernt der Dienst die Kamera beim ersten Klingeln |
| `LiveView`, `LiveViewSeconds` | Livebild ohne Klingeln im Menü, Dauer 15–600 s (Standard 60) |
| `Talkback` | Gegensprechen während eines Rufs (Standard `true`), braucht den Protect-Schlüssel |
| `TokenExpires`, `ProtectKeyExpires` | Ablaufdaten der Zugänge, z. B. `"2027-09-30"` |

Abweichungen für einen einzelnen PC: `C:\ProgramData\SIEntryDesk\sientrydesk.local.json`, z. B. `{ "LiveView": false }`
oder `{ "Talkback": false }` (der Installer schreibt sie mit `-NoLiveView` bzw. `-NoTalk`).

Die Pins ändern sich nur, wenn die Konsole ein neues Zertifikat bekommt. Dann ist das Symbol rot, und das Protokoll
nennt den erhaltenen Fingerabdruck. Erst prüfen, ob das Zertifikat wirklich erneuert wurde, dann den Pin anpassen.

## Schritt 2: Installieren
`sientrydesk.json` neben `install.ps1` legen, dann im entpackten Ordner, PowerShell **als Administrator**:
```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens
```
`-SetTokens` fragt verdeckt zwei Werte ab:
1. den Access-Token
2. den Protect-Schlüssel, leer = kein Livebild

Das Skript richtet ein:
1. prüft die `sientrydesk.json` (gültiges JSON, `Host` und `AccessPin` vorhanden)
2. beendet laufende Instanzen von Dienst und App
3. kopiert Programm und Admin-Skripte nach `C:\Program Files\SIEntryDesk`
4. richtet den Dienst unter dem eigenen Konto `NT SERVICE\SIEntryDesk` ein, mit Neustart bei Absturz
5. legt `C:\ProgramData\SIEntryDesk` an, lesbar nur für SYSTEM, Administratoren und den Dienst
6. übernimmt die `sientrydesk.json` dorthin
7. speichert die Zugänge (mit `-SetTokens`) oder lässt vorhandene stehen
8. trägt den Autostart für alle Benutzer ein, sperrt eingehende Verbindungen der App in der Firewall, legt den
   Eintrag im Startmenü an und startet Dienst und App

| Fall | Befehl |
|---|---|
| Erstinstallation | `powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens` |
| Update, Zugänge bleiben | `powershell -ExecutionPolicy Bypass -File .\install.ps1` |
| Livebild ohne Klingeln auf diesem PC aus | zusätzlich `-NoLiveView` |
| Gegensprechen auf diesem PC aus | zusätzlich `-NoTalk` |
| Nur die Zugänge erneuern | `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"` |
| Entfernen | `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\uninstall.ps1"`, mit `-KeepData` bleiben Konfiguration, Zugänge und Protokolle |

Danach: Symbol unten rechts grün, siehe [betrieb.md](betrieb.md#testen-nach-der-installation).

## Wo die Zugänge gespeichert sind
Aus den Tokens wird nichts abgeleitet, gespeichert werden die Tokens selbst, verschlüsselt:

| | |
|---|---|
| **Datei** | `C:\ProgramData\SIEntryDesk\secrets.dat` |
| **Verschlüsselung** | Windows DPAPI mit dem Schlüssel des Computers und einem festen Zusatzwert der App. Nur auf diesem PC zu entschlüsseln, kopiert ist die Datei wertlos |
| **Zugriff** | Der Ordner ist nur für SYSTEM, Administratoren und den Dienst `NT SERVICE\SIEntryDesk` lesbar, Standardbenutzer kommen nicht heran |
| **Wer sie verwendet** | Nur der Dienst. Die Tray-App kennt keine Tokens, sie bittet den Dienst über eine lokale Verbindung, z. B. um das Öffnen |

**Nicht in der Windows-Anmeldeinformationsverwaltung,** weil die pro Benutzer ist. Der Dienst läuft unter einem
eigenen Konto und braucht die Zugänge unabhängig davon, wer angemeldet ist.

**Grenze:** Ein lokaler Administrator kann die Datei entschlüsseln. Deshalb keine lokalen Adminrechte für Benutzer.

Gespeichert wird immer über den Dienst (`SIEntryDesk.Service.exe set-secrets`), egal ob mit `install.ps1 -SetTokens`,
`set-tokens.ps1` oder per Intune mit `set-tokens-intune.ps1`.

## Hilfe
`SIEntryDesk.exe --help` und `SIEntryDesk.Service.exe --help` zeigen alle Aufrufe.
