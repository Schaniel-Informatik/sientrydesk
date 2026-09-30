# SI EntryDesk – Installation und Pilot

Klingeln erkennen, Fenster mit Klingelton über allen Fenstern, Livebild mit Ton der Tür, Knopf „Öffnen“, Fenster
überall schliessen, sobald der Ruf endet. Livebild auch ohne Klingeln. Noch ohne Gegensprechen.

## Voraussetzungen
- Windows 11, x64, lokaler Administrator für die Installation
- Der PC erreicht die Konsole auf **TCP 12445** (Access), für das Livebild zusätzlich **TCP 443** und **TCP 7441**
- UniFi Access: **ein** API-Token (Access → Einstellungen → Allgemein → API Token), empfohlen **1 Jahr** gültig:
  **Gerät = Anzeigen, Standorte = Bearbeiten**, alles andere **Keinen**. Achtung: Damit lassen sich alle Türen öffnen.
  Der Dialog gibt für mehrere Bereiche „Bearbeiten“ vor, alles nicht Benötigte auf „Keinen“ stellen.
  (Getrennte Tokens zum Mithören und Öffnen sind möglich, bringen aber keine Sicherheit, weil beide auf dem PC liegen.)
- UniFi Protect, für das Livebild: API-Schlüssel (UniFi OS → Einstellungen → Control Plane → Integrations) und
  pro Türstation ein **RTSPS-Stream** in Protect (Kamera → Einstellungen → Erweitert). Die Stream-Adresse wirkt wie
  ein Zugangsschlüssel und gehört nicht in Dokumente oder Chats.

## Konfiguration: sientrydesk.json
Aus `sientrydesk.example.json` eine `sientrydesk.json` machen und neben `install.ps1` legen. Sie gilt für alle PCs
einer Anlage und enthält keine Geheimnisse.

Die Pins sind die Fingerabdrücke der Zertifikate der Konsole. Anzeigen, **nur im Firmennetz oder über VPN**:
```powershell
& '.\Service\SIEntryDesk.Service.exe' show-pins <Host der Konsole>
```

## Installieren
PowerShell **als Administrator**, im entpackten Ordner:
```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens
```
`-SetTokens` fragt verdeckt ab: den Access-Token, einen optionalen zweiten Token zum Öffnen (leer lassen), den
Protect-Schlüssel. Die Ablaufdaten gehören als `TokenExpires` und `ProtectKeyExpires` in die sientrydesk.json.
Ohne `-SetTokens` fragt das Skript nichts, geeignet für Updates und Intune. Gespeicherte Tokens bleiben erhalten.
`-NoLiveView` schaltet das Livebild ohne Klingeln auf diesem PC ab.

Tokens erneuern, ohne neu zu installieren:
```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"
```

## Bedienung
Klingel-Symbol unten rechts, eventuell unter dem Pfeil ^ in der Taskleiste.
- **Linksklick:** Status, die Türen für das Livebild und „Klingel pausieren“ (15 / 30 / 60 / 120 Minuten).
- **Rechtsklick:** dazu Version, „Ton der Tür automatisch einschalten“, Testklingeln, Beenden.

| Farbe | Bedeutung |
|---|---|
| grün | bereit |
| blau | Klingel pausiert, auf diesem PC kein Fenster und kein Ton. Verpasste Rufe stehen im Menü |
| grau | Konsole nicht erreichbar, z. B. ausserhalb des Firmennetzes ohne VPN. Keine Meldung |
| orange | Klingeln geht, aber etwas anderes nicht (Livebild) oder die Schlüssel laufen in 14 Tagen ab. Dann erscheint auch einmal pro Tag eine Meldung |
| rot | **dieser PC klingelt nicht** (Dienst, Token, Zertifikat). Nach 30 s erscheint eine Meldung |

„Ton an“ im Klingelfenster schaltet den Ton der Tür ein und den Klingelton aus. Nach dem Ende eines Rufs bleibt das
Fenster 10 s offen. Hat der Besucher abgebrochen (z. B. zweimal gedrückt) oder niemand abgenommen, lässt sich in
dieser Zeit noch öffnen. Während einer Teams-Besprechung
kann Windows andere Töne dämpfen (Sound → Weitere Soundeinstellungen → Kommunikation).

## Testen
1. **Testklingeln:** nur auf diesem PC, ohne Livebild, Öffnen gesperrt.
2. **Livebild ohne Klingeln:** Linksklick → Tür wählen. Ohne `DoorCameras` in der Konfiguration erscheint eine Tür
   erst nach ihrem ersten Klingeln.
3. **Echtes Klingeln:** Fenster, Klingelton, nach 1–2 s das Livebild.
4. **Am Handy abnehmen:** Das Fenster schliesst sich mit „Anderswo angenommen“.
5. **Öffnen am PC**, nur mit jemandem an der Tür. Im Access-Protokoll steht „<Benutzer> via SI EntryDesk (<PC>)“.
6. **Pause:** 15 Minuten pausieren, klingeln lassen. Es erscheint nichts, danach steht der Ruf im Menü.

## Wenn etwas nicht geht
Protokoll des Dienstes (nur für Administratoren lesbar): `C:\ProgramData\SIEntryDesk\logs\service-JJJJMMTT.log`.
Es enthält Türnamen, aber keine Tokens und keine Stream-Adressen.

## Entfernen
```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```
Mit `-KeepData` bleiben Konfiguration, Tokens und Protokolle erhalten.
