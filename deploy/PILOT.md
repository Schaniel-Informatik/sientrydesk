# SI EntryDesk – Pilot

**Etappe 1:** Klingeln erkennen, Fenster mit Klingelton über allen Fenstern, Knopf „Öffnen“, Fenster überall
schliessen, sobald der Ruf endet.
**Etappe 2:** Livebild mit Ton der Tür (zuerst stumm), nur während eines Rufs. Noch ohne Gegensprechen.

## Voraussetzungen
- Windows 11, x64, lokaler Administrator für die Installation
- Der PC erreicht die Konsole auf **TCP 12445** (Access), für das Livebild zusätzlich **TCP 443** und **TCP 7441**
- Zwei Access-Tokens (Access → Einstellungen → Allgemein → API Token):
  - zum Mithören: **Gerät = Anzeigen**, alles andere Keinen
  - zum Öffnen: **Standorte = Bearbeiten**, alles andere Keinen
- Für das Livebild: Protect-API-Schlüssel (UniFi OS → Einstellungen → Control Plane → Integrations) und in Protect
  pro Türstation ein freigegebener RTSPS-Stream
- SHA-256-Fingerprints der Konsole: Port 12445 (`-AccessPin`) und Port 443 (`-ProtectPin`). Port 7441 verwendet in
  der Regel dasselbe Zertifikat wie 443, sonst zusätzlich `-StreamPin`.

## Installieren oder aktualisieren
Ordner entpacken, PowerShell **als Administrator** öffnen, in den Ordner wechseln:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -ConsoleHost <IP der Konsole> -AccessPin '<Fingerprint 12445>' -ProtectPin '<Fingerprint 443>'
```

Das Skript fragt die Tokens verdeckt ab: Access-Token, Token zum Öffnen, Protect-Schlüssel.
Optional nur bestimmte Türen: `-Doors 'Türname 1','Türname 2'`. `-KeepTokens` behält die gespeicherten Tokens.
**Beim Update von 0.1.x auf das Livebild `-KeepTokens` weglassen**, damit der Protect-Schlüssel dazukommt.

Die App startet am Ende der Installation und danach bei jeder Anmeldung, von Hand über das Startmenü („SI EntryDesk“).
Unten rechts erscheint ein Klingel-Symbol, eventuell erst unter dem Pfeil ^ in der Taskleiste: **grün** = bereit, **orange** = Problem (Text im Menü),
**grau** = Dienst nicht erreichbar. Die erste Zeile im Menü zeigt die Version.

## Testen
1. **Testklingeln:** Rechtsklick auf das Symbol → „Testklingeln“. Nur auf diesem PC, ohne Livebild, Öffnen gesperrt.
2. **Echtes Klingeln:** Nach 1–2 s erscheint das Livebild. Unter dem Bild „Livebild“ und der Knopf „Ton an“.
3. **Am Handy abnehmen:** Das Fenster schliesst sich mit „Anderswo angenommen“.
4. **Öffnen am PC**, nur mit jemandem an der Tür. Im Access-Protokoll steht „<Benutzer> via SI EntryDesk (<PC>)“.
5. Während das Fenster offen ist, in einem anderen Programm weitertippen: Die Eingabe muss dort bleiben.

## Wenn etwas nicht geht
Protokoll des Dienstes (nur für Administratoren lesbar): `C:\ProgramData\SIEntryDesk\logs\service-JJJJMMTT.log`.
Es enthält Türnamen, aber keine Tokens und keine Stream-Adressen.

## Entfernen
```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```
Mit `-KeepData` bleiben Konfiguration, Tokens und Protokolle erhalten.
