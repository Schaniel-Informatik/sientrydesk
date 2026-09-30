# Gegensprechen (Stufe 2): Grundlagen und Entscheide

Stand 2026-09-30, Entscheide offen.

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

## Offene Entscheide
| # | Frage | Möglichkeiten | Empfehlung |
|---|---|---|---|
| 1 | **Bedienung** | a) Taste halten zum Sprechen · b) Klick schaltet ein und aus · c) freies Sprechen in beide Richtungen gleichzeitig | **b)** Klick ein und aus. Während man spricht, ist der Ton der Tür am PC stumm, sonst gibt es Echo über Lautsprecher und Mikrofon. Nach 30 s Stille schaltet es selbst aus. c) bräuchte Echo-Unterdrückung und geht zuverlässig nur mit Headset |
| 2 | **Wann** | a) nur während eines Klingelrufs (plus 10 s Nachfrist) · b) auch beim Livebild ohne Klingeln | **a)** Sonst wird die App zur Lautsprecheranlage an der Tür, ohne dass jemand geklingelt hat |
| 3 | **Mehrere PCs** | a) immer nur einer gleichzeitig, die anderen sehen „<Name> spricht mit der Tür“ · b) frei | **a)** Zwei Stimmen gleichzeitig an der Tür sind unverständlich. Die anderen PCs stoppen zudem ihren Klingelton |
| 4 | **Mikrofon** | a) Windows-Standard für Kommunikation · b) Auswahl im Menü der App | **a)** für den Anfang. Die Auswahl kommt, falls ein Arbeitsplatz ein anderes Gerät braucht |
| 5 | **Protokoll** | jede Sprechphase mit Benutzer, Tür und Dauer im Dienstprotokoll | **ja** |

## Umsetzung (nach den Entscheiden)
- Mikrofon: NAudio (WASAPI), Opus-Kodierung: Concentus (reines C#). Beide Bibliotheken mit freier Lizenz.
- RTP-Pakete selbst gebaut (einfacher Kopf mit Sequenznummer und Zeitstempel, 20 ms pro Paket).
- Test nur mit jemandem an der Tür: Verständlichkeit, Lautstärke, Verzögerung, Echo mit Lautsprechern und mit Headset.
  Ausserdem: Was passiert, wenn gleichzeitig jemand über die Mobile-App spricht? Klingeln die Handys wirklich weiter?
  Erreichen die PCs im Büronetz die Türstationen per UDP (bisher nur über das VPN geprüft)?
