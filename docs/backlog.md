# Offene Punkte für den nächsten Build

Gesammelt während des Pilots, noch nicht umgesetzt. Entscheide von Marcel, Stand 2026-09-29.

- **Programm-Icon:** Klingel auf grünem Kreis wie das Tray-Symbol, 16–256 px. Für Programmdatei, Startmenü,
  Taskleiste und Fenstertitel. Heute steht dort das Windows-Standardsymbol.
- **Linksklick auf das Tray-Symbol:** kurzes Menü mit Status und den Türen für das Livebild. Das Rechtsklick-Menü
  bleibt wie bisher mit Version, Testklingeln und Beenden.
- **Einträge mit Protect-Kameranamen**, ohne vorangestelltes „Livebild“, also zum Beispiel nur „Eingang Nord“ statt „Livebild Eingang Nord“.
  Der Dienst holt die Namen über die Protect-API (Kamera-ID aus der gelernten Zuordnung). Fehlt der Name, wird der
  Access-Türname angezeigt.
- **Statusfarben:** Rot = dieser PC klingelt nicht (Dienst nicht erreichbar oder keine Verbindung zu Access),
  mit einer Windows-Benachrichtigung nach etwa 30 s. Orange = Klingeln geht, anderes nicht (z. B. Livebild).
  Grün = bereit.
- **Livebild ohne Klingeln: Standard 60 s statt 90 s** (`LiveViewSeconds`, Installer-Vorgabe mit ändern).
- **Klingel pausieren** (im Linksklick-Menü): 15 / 30 / 60 / 120 Minuten, danach automatisch wieder an.
  Während der Pause „Pausiert bis HH:mm · Fortsetzen“.
  - Wirkung: kein Fenster und kein Ton, nur dieser PC und dieser Benutzer. Der Dienst läuft weiter, andere PCs und
    die Handys klingeln normal.
  - Symbol blau mit Pausenzeichen. Rot hat Vorrang: Wenn der PC nicht klingeln könnte, zeigt das Symbol das auch
    während der Pause.
  - Verpasste Rufe im Menü: Anzahl, letzte Zeit und Tür.
  - Die Pause wird nicht gespeichert. Ein Neustart der App oder des PCs beendet sie.
  - Bewusst **ohne** „nur stumm“ und **ohne** automatisches Pausieren (Präsentation, Vollbild, Nicht stören),
    damit es für alle Benutzer verständlich bleibt.
- **Ton der Tür an einem echten PC prüfen** („Ton an“ im Klingelfenster). Die Türstationen liefern Ton, auf der
  Test-VM über Fernwartung war er nicht zu hören.
- **Beobachten:** Nach der Installation von 0.2.4 kam das erste Livebild erst nach einiger Zeit, nach einem Neustart
  des Dienstes sofort. Ursache unklar.
