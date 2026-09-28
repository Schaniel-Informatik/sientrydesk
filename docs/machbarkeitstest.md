# Machbarkeitstest

Testumgebung: UniFi-OS-Konsole mit Protect und Access, zwei Türstationen (G6 Pro Entry, G6 Entry), zwei UA Hub Door.
Getestet vom Mac über VPN mit `tools/feasibility/sied.py`. Keine Namen, Adressen oder IDs in diesem Protokoll.

| Datum | Prüfung | Ergebnis |
|---|---|---|
| 2026-09-26 | TLS mit Pinning | ✅ Port 443 und 7441 verwenden das UniFi-OS-Zertifikat (selbst ausgestellt, bis 2028). Port 12445 (Access-API) hat ein eigenes (bis 2056). Pins bei der ersten Verbindung über VPN übernommen |
| 2026-09-26 | Protect Integration API | ✅ erreichbar mit API-Schlüssel aus UniFi OS → Integrations, Protect 7.2.105. Beide Türstationen melden Mikrofon und Lautsprecher |
| 2026-09-26 | Talkback-Sitzung | ✅ `POST talkback-session` liefert `rtp://<türstation>:7004`, Opus, 24 kHz, 16 Bit. Die Gegenstelle ist die Türstation selbst, nicht die Konsole |
| 2026-09-26 | RTSPS-Stream | an den Türstationen zunächst nicht freigegeben |
| 2026-09-28 | RTSPS-Stream anlegen (G6 Pro Entry) | ✅ `POST rtsps-stream` mit Qualität `medium`. Bild: **H.265/HEVC Main, 1440 × 1920 (Hochformat), 20 fps**. Ton: AAC-LC 16 kHz mono und Opus 48 kHz stereo. Die Stream-Adresse zeigt auf die Konsole, Port 7441 |
| 2026-09-28 | RTSPS-Stream anlegen (G6 Entry) | ✅ Qualität `medium` bei der G6 Entry: **H.265, 720 × 960, 30 fps**, Ton wie bei der G6 Pro Entry. Über den Stream-Proxy des Dienstes 15 s flüssig (450 Bilder) |
| 2026-09-28 | **Livebild unter Windows (Pilot 0.2.2)** | ✅ LibVLC zeigt beide Türstationen über den Stream-Proxy des Dienstes (TLS mit Pin, Einmal-Adresse), das Fenster schliesst sich zur eingestellten Zeit. LibVLC löst beim ersten Abspielen eine Firewall-Abfrage für eingehende Verbindungen aus, die App braucht keine. Ab 0.2.3 sperrt der Installer das per Regel |
| 2026-09-28 | Livebild mit Verzögerung | ✅ ca. **1 s** hinter der Wirklichkeit (ffplay mit `nobuffer`/`low_delay`, über VPN) |
| 2026-09-28 | TLS für den Stream | ✅ ffmpeg 9 prüft Zertifikate von sich aus. Mit dem gepinnten Zertifikat als `-ca_file` und `-verifyhost unifi.local` (steht im Zertifikat) läuft es mit voller Prüfung |
| 2026-09-28 | Ton an der Tür (`talkback --tone`) | ✅ **Ton an der Tür gehört.** Opus 24 kHz mono per RTP vom Mac über VPN direkt zur Türstation |
| 2026-09-26 | Access Developer API | ✅ mit Token aus der Access-App (Einstellungen → Allgemein → API Token), Rechte `view:device` und `view:space`. Ein Schlüssel aus UniFi OS → Integrations wird mit `CODE_UNAUTHORIZED` abgelehnt, die Zugänge sind getrennt |
| 2026-09-26 | Access-Geräteliste | ⚠️ zeigt nur die beiden UA Hub Door, nicht die Türstationen. Diese sind reine Protect-Geräte |
| 2026-09-26 | WebSockets Access und Protect | ✅ beide verbinden mit Pinning. Protect beantwortet WebSocket-Pings, die Verbindung bleibt stehen |
| 2026-09-26 | Lebenszeichen Access-WebSocket | ⚠️ Access beantwortet **keine WebSocket-Pings**, mit Standard-Pings bricht die Verbindung alle 50 s ab. Access sendet stattdessen alle 5 s ein `"Hello"` (Text). Mit abgeschalteten Pings und Neuaufbau nach 15 s Stille lief die Verbindung im Test stabil |
| 2026-09-28 | Klingel-Ereignis Access (`remote_view`) bei G6 Pro Entry | ✅ kommt, obwohl die Türstation nicht in der Access-Geräteliste steht. `device_type` = `UVC G6 Pro Entry`, `door_name` = zugehöriger Hub. Dreimal geklingelt, dreimal gemeldet |
| 2026-09-28 | Klingel-Ereignis Protect (`ring`) | ✅ kommt, aber **doppelt**: zwei `add`-Ereignisse je Tastendruck mit verschiedenen IDs (UUID und 24-stellige Hex-ID), gleiches Gerät, bis 0,6 s auseinander. Kein `update` mit Ende |
| 2026-09-28 | Zeitlicher Abstand Access ↔ Protect | Access und Protect melden innerhalb von etwa 0,3 s, mal das eine, mal das andere zuerst. Gegenüber dem Zeitstempel im Ereignis: Access ca. 1 s (nur sekundengenau), Protect 2 bis 3,5 s (vermutlich teils Uhrenabweichung zwischen Konsole und Mac) |
| 2026-09-28 | „Anderswo angenommen“ (`reason_code` 400) | ✅ kommt, sobald jemand am Handy abnimmt, mit `remote_call_request_id` des Rufs |
| 2026-09-28 | Öffnen im Ruf durch andere Person | ✅ Reihenfolge: `remote_view.change` 400, dann `access.data.device.remote_unlock`, dann ein weiteres `remote_view.change` mit `reason_code` 0 **ohne** Request-ID. `access.logs.add` nennt Benutzer und `credential_provider` = `CALL` |
| 2026-09-28 | Ruf abgelehnt (`reason_code` 106) | ✅ kommt, wenn im Ruf das Öffnen abgelehnt wird |
| 2026-09-28 | Ruf ohne Antwort (`reason_code` 105) | ✅ Zeitüberschreitung **nach genau 60 s** mit `remote_call_request_id` des Rufs |
| 2026-09-28 | Rechte der Access-Tokens | ⚠️→✅ Der Dialog „Neues API-Token“ gibt für mehrere Bereiche „Bearbeiten“ vor. Beide Tokens hatten zuerst mehr Rechte als vorgesehen und wurden neu angelegt. `sied.py scopes` bestätigt: Lese-Token nur Gerät + Standorte, Öffnen-Token nur Standorte (Bearbeiten schliesst Anzeigen ein) |
| 2026-09-28 | **Tür öffnen per API** (Pilot Etappe 1, Windows 11) | ✅ `PUT /doors/{id}/unlock` mit eigenem Token (nur Standorte = Bearbeiten). Bestätigung nach **0,6 s**. Im Access-Protokoll steht `actor_name` („Benutzer via SI EntryDesk (PC)“) |
| 2026-09-28 | Rufende nach Öffnen per API | ⚠️ Access meldet danach `reason_code` **108** („Besucher hat abgebrochen“), nicht 107. Reihenfolge: `remote_unlock`, dann 108, dann `logs.add` mit dem Namen. Die App gibt der Öffnung in der Anzeige Vorrang (ab 0.1.1) |
| 2026-09-28 | Öffnen am Handy, Anzeige in der App | ✅ „Anderswo angenommen“, danach „Tür geöffnet von <Name>“ aus `access.logs.add` |
| 2026-09-28 | Dauerbetrieb der WebSockets | ✅ 30 min ohne Abbruch, Access über `"Hello"` überwacht (Neuaufbau nach 15 s Stille), Protect mit Pings |
| | Tür öffnen | offen, nur mit Freigabe und jemandem vor Ort |

## Noch offen
1. Zweite Türstation (G6 Entry) gegenprüfen: Klingeln, Stream, Talkback.

## Erkenntnisse für das Design
- **Access als Hauptquelle für den Rufstatus:** Klingeln und Ende kommen beide über Access, mit derselben Request-ID.
  Protect nur ergänzend, zum Beispiel für die Kamera-ID, und dann mit Entdoppelung.
- `remote_view.change` ohne Request-ID oder mit unbekanntem `reason_code` ignorieren, statt ein Fenster zu schliessen.
- Aus `access.logs.add` lässt sich in den anderen Fenstern anzeigen, wer geöffnet hat.
- **Video:** Der Player muss HEVC können (LibVLC kann das auch ohne die Windows-HEVC-Erweiterung) und TLS gegen den Pin
  prüfen. Das Popup ist hochkant.
- **Gegensprechen ist machbar:** Die App nimmt das Mikrofon auf, kodiert als Opus 24 kHz und schickt es per RTP an die
  Adresse aus der Talkback-Sitzung. Hören läuft über die Tonspur des RTSPS-Streams.
- Die Client-PCs brauchen für Talkback **UDP direkt zu den Türstationen**, nicht nur Verbindungen zur Konsole.
- Der Protect-Schlüssel und der Access-Token sind zwei verschiedene Zugänge. Beide gehören in die App-Konfiguration.
- Das UniFi-OS-Zertifikat läuft 2028 ab und wird dann wahrscheinlich neu erzeugt. Die App braucht einen
  sicheren Weg, den Pin zu erneuern.
