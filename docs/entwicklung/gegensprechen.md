# Gegensprechen (Stufe 2): Grundlagen und Entscheide

Stand 2026-10-02, entschieden, noch nicht umgesetzt.

## Wie es technisch geht
- **Hören** läuft bereits: Der Ton der Türstation kommt mit dem Livebild über die geprüfte RTSPS-Verbindung.
- **Sprechen** geht über die offizielle Protect-Schnittstelle: `POST /v1/cameras/{id}/talkback-session` liefert eine
  Zieladresse `rtp://<Türstation>:7004` und das Format **Opus, 24 kHz, mono**. Der PC nimmt das Mikrofon auf, kodiert es
  und schickt es per RTP direkt an die Türstation. Im Machbarkeitstest (2026-09-28) kam ein Testton an der Tür an.
- Die Talkback-Sitzung legt der **Dienst** an, weil nur er den Protect-Schlüssel hat. Er entscheidet auch, wann
  gesprochen werden darf, und protokolliert es. Die App schickt den Ton selbst an die Türstation.

## Grenzen, die sich nicht ändern lassen
- **Der Ton zur Tür ist unverschlüsselt** (RTP über UDP im Firmennetz). So hat Ubiquiti die Schnittstelle gebaut.
  Der Rückweg, also Bild und Ton von der Tür, bleibt verschlüsselt und geprüft.
- **Die PCs brauchen UDP zu den Türstationen** (Port 7004), nicht nur Verbindungen zur Konsole. Über das VPN
  funktionierte das im Test.
- **Die Handys klingeln weiter.** Die Developer-API kann einen Ruf nicht „annehmen“. Spricht jemand am PC, klingeln
  Mobile-Apps und die Access-Web-App bis zum Abnehmen, Öffnen oder zur Zeitüberschreitung (60 s) weiter.
- Das Livebild ist etwa 1 s verzögert. Antworten kommen also leicht verspätet an.

## Entscheide (2026-10-02, Marcel)
| # | Frage | Entscheid | Folgen für die Umsetzung |
|---|---|---|---|
| 1 | **Bedienung** | **Taste halten zum Sprechen** (abweichend von der Empfehlung „Klick ein und aus“) | Gesprochen wird nur, solange der Knopf „Sprechen“ gedrückt ist. Solange ist der Ton der Tür am PC stumm, sonst gibt es Echo über Lautsprecher und Mikrofon. Loslassen beendet sofort. Schutz gegen eine hängende Taste: nach 60 s am Stück endet das Sprechen von selbst |
| 2 | **Wann** | nur während eines Klingelrufs, plus 10 s Nachfrist | Erzwingt der Dienst, wie beim Öffnen. Beim Livebild ohne Klingeln gibt es keinen Knopf „Sprechen“ |
| 3 | **Mehrere PCs** | ursprünglich „immer nur einer“; **geändert 2026-10-02:** alle PCs dürfen sprechen, pro PC abschaltbar | Jeder PC hat seinen eigenen Dienst, eine Stelle zwischen den PCs gibt es nicht, und weder Access noch Protect melden, dass an einer Kamera gesprochen wird. Abstimmung zwischen PCs ginge nur mit eigenem Netzprotokoll, das ist verworfen. Auf einem PC spricht immer nur einer. Abschalten wie beim Livebild: `"Talkback": false` in der sientrydesk.local.json bzw. `-NoTalk` |
| 4 | **Mikrofon** | Windows-Standard für Kommunikation | Keine Auswahl in der App. Die Mikrofonfreigabe von Windows für Desktop-Apps muss an sein |
| 5 | **Protokoll** | ja | Jede Sprechphase mit Benutzer, PC, Tür und Dauer im Dienstprotokoll |

## Umsetzung
- Mikrofon: NAudio (WASAPI), Opus-Kodierung: Concentus (reines C#). Beide Bibliotheken mit freier Lizenz.
- RTP-Pakete selbst gebaut (einfacher Kopf mit Sequenznummer und Zeitstempel, 20 ms pro Paket).
- Test nur mit jemandem an der Tür: Verständlichkeit, Lautstärke, Verzögerung, Echo mit Lautsprechern und mit Headset.
  Ausserdem: Was passiert, wenn gleichzeitig jemand über die Mobile-App spricht? Klingeln die Handys wirklich weiter?
  Erreichen die PCs im Büronetz die Türstationen per UDP (bisher nur über das VPN geprüft)?
