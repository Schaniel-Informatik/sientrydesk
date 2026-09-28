# SI EntryDesk – Pilot, Etappe 1

Diese Etappe kann: Klingeln erkennen, Fenster mit Klingelton über allen Fenstern, Knopf „Öffnen“, Fenster
überall schliessen, sobald der Ruf endet. Noch **ohne Livebild** und ohne Gegensprechen.

## Voraussetzungen
- Windows 11, x64, lokaler Administrator für die Installation
- Der PC erreicht die Konsole auf **TCP 12445** (Access-API)
- Zwei Access-Tokens (Access → Einstellungen → Allgemein → API Token):
  - zum Mithören: **Gerät = Anzeigen**, alles andere Keinen
  - zum Öffnen: **Standorte = Bearbeiten**, alles andere Keinen
- SHA-256-Fingerprint des Zertifikats auf Port 12445

## Installieren
Ordner entpacken, PowerShell **als Administrator** öffnen, in den Ordner wechseln:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -ConsoleHost <IP der Konsole> -AccessPin '<Fingerprint>'
```

Optional nur bestimmte Türen: `-Doors 'Türname 1','Türname 2'`. Ohne Angabe zeigt der PC alle Türen.
Das Skript fragt die beiden Tokens verdeckt ab. Für ein Update mit denselben Tokens `-KeepTokens` anhängen.

Danach erscheint unten rechts ein Klingel-Symbol: **grün** = bereit, **orange** = Problem (Text im Menü),
**grau** = Dienst nicht erreichbar.

## Testen
1. **Testklingeln:** Rechtsklick auf das Symbol → „Testklingeln“. Fenster und Klingelton erscheinen nur auf diesem
   PC, Öffnen ist dabei gesperrt. Nach 10 s schliesst es sich.
2. **Echtes Klingeln, nicht abnehmen:** Fenster erscheint, nach 60 s „Niemand hat abgenommen“.
3. **Am Handy abnehmen:** Das Fenster auf dem PC schliesst sich mit „Anderswo angenommen“.
4. **Öffnen am PC**, nur mit jemandem an der Tür: Die Tür geht auf, im Access-Protokoll steht
   „<Benutzer> via SI EntryDesk (<PC>)“.
5. Während das Fenster offen ist, in einem anderen Programm weitertippen: Das Fenster darf die Eingabe nicht übernehmen.

## Wenn etwas nicht geht
Protokoll des Dienstes (nur für Administratoren lesbar): `C:\ProgramData\SIEntryDesk\logs\service-JJJJMMTT.log`.
Es enthält Türnamen, aber keine Tokens.

## Entfernen
```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```
Mit `-KeepData` bleiben Konfiguration, Tokens und Protokolle erhalten.
