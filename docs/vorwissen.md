# Vorwissen aus der Praxis

Stand 2026-09. Übernommen aus einem Kundeneinsatz, ohne Kundendaten. Was davon im Machbarkeitstest
bestätigt oder widerlegt wird, gehört ins Testprotokoll.

## Warum es die App braucht
- UniFi Access hat bewusst **keine Windows-App**. Der offizielle Weg ist die Access-Web-App unter
  `unifi.ui.com` mit Browser-Benachrichtigungen. Das setzt voraus, dass der Owner auf der Konsole
  **Remote Access** (Cloud-Zugang) aktiviert. Bei der Push-Zustellung spielt zusätzlich UniFi Endpoint mit.
  Ergebnis in der Praxis: viele Klicks, schwache Meldung.
- Hardware-Alternative: der **Intercom Viewer** (Tischgerät mit Klingel, Bild, Gegensprechen, Öffnen).
  Er passt für einen festen Empfangsplatz. Die App lohnt sich, wenn mehrere Personen am normalen PC
  reagieren sollen.

## Wer den Klingelruf bekommt (Access-Konfiguration)
- Der **UniFi Owner ist automatisch Door Attendant** für alle Standorte und bekommt deshalb Klingelmeldungen.
  Abschalten lässt sich nur die Zustellung, nicht die Rolle.
- Bei Hubs mit Intercom oder Pro Reader, dazu gehört die G6 Entry, ersetzen **Doorbell Call Receivers**
  die Door Attendants. Das ist die Stelle, an der festgelegt wird, wer klingelt.
- **Offen für den Test:** Kommt `access.remote_view` über den API-Token unabhängig von dieser Empfängerliste
  an? Und löst ein Klingeln an der G6 Entry (ein Protect-Gerät) überhaupt ein Access-Ereignis aus, oder nur
  ein Protect-Ring-Ereignis?

## Bestehende Projekte (nur als Referenz, keinen Code übernehmen)
Keines davon ist eine Windows-App mit Bild und Ton:
- Home Assistant, Integration `unifi_access`
- openHAB-Binding für UniFi Access (Developer API, WebSocket)
- `hubitat-unifi-access`
- `unifi-doorbell-chime` (PC-Benachrichtigung bei Protect-Türklingeln)

Code daraus nicht kopieren. Das Repository wird MIT-lizenziert und öffentlich, fremder Code bräuchte eine
Lizenzprüfung.

## Netzwerk-Voraussetzungen der Client-PCs
- Access Developer API: Port **12445** auf der Konsole
- Protect Integration API: über UniFi OS (HTTPS 443)
- Videostream RTSPS: üblicherweise Port **7441**, im Test bestätigen
- Die nötigen Firewall-Freigaben dokumentiert die App. Einrichten muss sie der Netzwerk-Admin.

## Verteilung über Intune (Muster aus einer früheren Win32-Paketierung)
- Win32-App, Installation im **System-Kontext**, Zuweisung an **Gerätegruppen**. So werden auch Geräte ohne
  angemeldeten Zielbenutzer erreicht.
- **Keine Selbst-Updates** im Benutzerkontext, weil Standardbenutzer keine Admin-Rechte haben. Updates kommen als
  neue Intune-App-Version mit Supersedence.
- MSI statt EXE-Bootstrapper, damit Installationsparameter zuverlässig ankommen. Der ProductCode ändert sich
  mit jeder Version, die Erkennungsregel muss dazu passen.
- Ist auf einem Gerät schon eine **neuere** Version manuell installiert, bricht die Installation mit Fehler
  **1638** ab.

## Offene Designfrage für den MVP
Die Tray-App läuft im Benutzerkontext, der Windows-Anmeldeinformationsspeicher ist pro Benutzer. Wie kommt
der API-Token auf die PCs, ohne dass er im Klartext in einem Intune-Skript oder Paket steht? Klären, bevor
die Verteilung gebaut wird.
