# API-Notizen

Aus der offiziellen Dokumentation, Stand 2026-09. Quellen:
- UniFi Access API Reference (PDF): https://assets.identity.ui.com/unifi-access/api_reference.pdf
- UniFi Protect Integration API (OpenAPI, v7.1.87): https://developer.ui.com/protect/v7.1.87/openapi.json

## UniFi Access Developer API
- Host `https://<konsole>:12445`, Header `Authorization: Bearer <token>`. Das Zertifikat ist selbst ausgestellt.
- Nicht mehr verfügbar nach einem Upgrade auf Identity Enterprise.

| Zweck | Endpunkt | Recht |
|---|---|---|
| Ereignisse (WebSocket, ab Access 1.20.11) | `GET /api/v1/developer/devices/notifications` | `view:device` |
| Geräte | `GET /api/v1/developer/devices` | `view:device` |
| Türen | `GET /api/v1/developer/doors` | `view:space` |
| Tür öffnen | `PUT /api/v1/developer/doors/{id}/unlock` | `edit:space` |
| Klingeln auslösen (Intercom, Reader Pro, ab 4.0.10) | `POST /api/v1/developer/devices/{id}/doorbell` | `edit:device` |

**Ereignisse:**
- `access.remote_view`: Es klingelt. Enthält `request_id`, `door_name`, `device_type` und `create_time` in Sekunden.
- `access.remote_view.change`: Der Ruf ist beendet. Das Feld `remote_call_request_id` verweist auf den Ruf, `reason_code` nennt den Grund:
  105 Zeitüberschreitung, 106 abgelehnt, 107 geöffnet, 108 Besucher bricht ab, **400 anderswo angenommen**.
- `access.data.device.remote_unlock`: Die Tür wurde per Fernzugriff geöffnet.
- **Lebenszeichen (im Test ermittelt, nicht dokumentiert):** Beim Verbinden kommt `access.base.info`, danach alle 5 s
  der Text `"Hello"`. WebSocket-Pings des Clients beantwortet der Server nicht. Der Client darf also keine Pings
  erwarten und muss die Verbindung anhand der `"Hello"`-Nachrichten überwachen.

**IDs im Klingel-Ereignis (im Test ermittelt):**
- `door_id` ist die Tür-ID für `PUT /doors/{id}/unlock`. `access.data.device.remote_unlock` liefert sie als `data.unique_id`.
- `connected_uah_id` ist die ID des UA Hub. `remote_unlock` liefert sie als `event_object_id`, `access.logs.add`
  als Ziel vom Typ `door`.
- `device_id` ist bei Türstationen aus Protect (`is_camera` = true) die **Protect-Kamera-ID**. Damit ist die Kamera für
  das Livebild direkt bekannt.
- `unlocking_not_allowed`: Wenn gesetzt, erlaubt Access für diesen Ruf kein Öffnen.
- `access.logs.add` mit `event.type` = `access.door.unlock` nennt in `actor.display_name`, wer geöffnet hat.

**Öffnen:** Im Test mit einer aus PC und Benutzer abgeleiteten UUID als `actor_id` erfolgreich. Nach dem Öffnen per
API endet der Ruf mit `reason_code` 108, nicht 107. Im Body lassen sich `actor_id` und `actor_name` mitgeben, sie erscheinen im Access-Protokoll. Ohne diese Angaben
steht dort der Name des Tokens. Für die App bietet sich an, PC und Benutzer zu übergeben.

## UniFi Protect Integration API
- Lokal `https://<konsole>/proxy/protect/integration/v1/...`, Header `X-API-KEY`.

| Zweck | Endpunkt |
|---|---|
| Ereignisse (WebSocket) | `GET /v1/subscribe/events` |
| Kameras (`featureFlags.hasMic`, `hasSpeaker`) | `GET /v1/cameras` |
| RTSPS-Stream lesen / anlegen / entfernen | `GET` / `POST` / `DELETE /v1/cameras/{id}/rtsps-stream` |
| Talkback-Sitzung | `POST /v1/cameras/{id}/talkback-session` |
| Standbild | `GET /v1/cameras/{id}/snapshot` |

- **Klingeln:** Die Nachricht hat `type` = `add`, das Feld `item.type` ist `ring` und `item.start` ist ein Zeitstempel in Millisekunden.
  Ein späteres `update` liefert das Ende. Ein Ereignis „anderswo angenommen“ gibt es in Protect **nicht**.
- **Stream:** `rtsps://<konsole>:7441/<pfad>?enableSrtp` in den Qualitäten high, medium und low. Der Pfad wirkt wie ein
  Zugangsschlüssel und gehört nicht in Logs.
- **RTSP-Ablauf (im Test mitgeschnitten):** OPTIONS, DESCRIBE, SETUP je Spur (`trackID=0` AAC, `1` Opus, `2` H.265),
  PLAY, alles über dieselbe TLS-Verbindung (`RTP/AVP/TCP`, interleaved). Anfragen tragen die volle Adresse, Antworten
  nur `Content-Base`. Das SDP verweist relativ auf die Spuren. Laut `Public` nimmt der Server auch `ANNOUNCE` und
  `RECORD` an.
- **Talkback:** Antwort z. B. `{"url": "rtp://<ip>:7004", "codec": "opus", "samplingRate": 24000, "bitsPerSample": 16}`.
  Die Adresse im Beispiel ist die des Geräts. Die Client-PCs brauchen dann UDP direkt zur Türstation.

## Folgen für das Design
- Das Fenster überall zu schliessen, wenn jemand anders abnimmt, geht nur über Access (`reason_code` 400).
  Ob Access bei der G6 Entry überhaupt `remote_view` sendet, klärt der Machbarkeitstest.
- Protect liefert Bild, Ton und Talkback, Access das Öffnen und den Rufstatus. Die App braucht beide Zugänge.
