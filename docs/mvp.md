# Erste Version (Pilot)

Pilot an 1–2 Windows-11-PCs. Grundlage ist der Machbarkeitstest (`machbarkeitstest.md`).

## Architektur: Dienst und Tray-App (Entscheid 2026-09-28)
- **SIEntryDesk-Dienst** (Windows-Dienst, Systemkonto): hält die Tokens, die Verbindungen zu Access und Protect und die
  Regel, wann geöffnet werden darf. Die Tokens sind für Standardbenutzer nicht lesbar.
- **SIEntryDesk-App** (Tray, WPF, pro Benutzersitzung): zeigt an und bedient, spricht nur mit dem lokalen Dienst.
- **Grund:** `edit:space` lässt sich nicht einschränken. Wer diesen Token hat, kann alle Türen öffnen oder dauerhaft
  offen halten und Lockdown oder Evakuierung auslösen. Deshalb bekommt die Benutzer-App den Token nicht.
- Später ausbaubar zu einem zentralen Dienst auf einem Server.

## Livebild über den Dienst (Entscheid 2026-09-28)
- LibVLC nutzt für RTSP die Bibliothek live555. Deren TLS-Client **prüft das Serverzertifikat nicht**. Wer sich im Netz
  als Konsole ausgibt, könnte ein falsches Bild zeigen, etwa eine harmlose Person, während jemand anderes vor der Tür steht.
- Deshalb baut der **Dienst** die RTSPS-Verbindung mit gepinntem Zertifikat selbst auf. An LibVLC reicht er den Stream
  unter einer **Einmal-Adresse** `rtsp://127.0.0.1:<port>/<token>` weiter. Sie gilt nur für einen laufenden Ruf und wird
  10 s nach dessen Ende geschlossen.
- Die App sieht die dauerhafte Stream-Adresse nie, denn der Pfad wirkt wie ein Zugangsschlüssel.
- **Livebild ohne Klingeln (Entscheid Marcel, 2026-09-28):** über das Tray-Menü, pro PC abschaltbar (`LiveView`,
  Standard ein). Jeder Abruf steht mit Windows-Benutzer im Protokoll des Dienstes, die Adresse endet nach 90 s
  (`LiveViewSeconds`, 15–600).
  Die Kamera einer Tür lernt der Dienst beim ersten Klingeln. Access nennt die Türstationen nicht in der Türstruktur,
  Protect kennzeichnet sie nicht.
- Einmal-Adressen enden genau zum Ablauf, auch laufende Verbindungen: beim Klingeln 10 s nach Rufende, spätestens nach
  3 Minuten.
- **Nur RTP über die RTSP-Verbindung (TCP interleaved).** Bei UDP schickt Protect Bild und Ton unverschlüsselt direkt an
  den PC, am Proxy und am Pin vorbei. Der Dienst lehnt solche `SETUP` mit „461 Unsupported Transport“ ab, Player
  wechseln dann auf TCP. Gefunden im Pilot: LibVLC versuchte trotz `:rtsp-tcp` zuerst UDP.
- Der Dienst leitet nur Befehle zum Abspielen weiter. `ANNOUNCE`/`RECORD` würde der Protect-Server annehmen,
  damit liesse sich ein fremdes Bild einspeisen.

## Umfang
1. Tray-App mit Autostart. Der Dienst baut Verbindungen selbst neu auf und überwacht Access über das `"Hello"` alle 5 s.
2. Klingeln: Fenster hochkant über allen anderen Fenstern, ohne die Tastatur zu übernehmen. Klingelton in Schleife,
   Windows-Benachrichtigung. Jeder PC zeigt nur die für ihn eingestellten Türen.
3. Livebild mit Ton von der Tür (LibVLC, H.265), TLS gegen den Pin, Ton stummschaltbar.
4. Knopf „Öffnen“: Der Dienst öffnet nur während eines laufenden Rufs und nur die klingelnde Tür, einmal pro Ruf.
   Im Access-Protokoll erscheinen Windows-Benutzer und PC (`actor_name`).
5. Fenster überall schliessen beim Ende des Rufs (`remote_view.change`), vorher kurz „Geöffnet von …“ bzw.
   „Anderswo angenommen“ anzeigen.
6. Lokales Protokoll ohne Tokens und ohne Stream-Adressen.

## Bewusst später
- Gegensprechen in der App (Mikrofon → Opus 24 kHz → RTP). Die Machbarkeit ist nachgewiesen. Bis dahin öffnet der
  Knopf „Sprechen“ die UniFi-App.
- MSI-Paket und Verteilung über Intune.
- Einstellungsoberfläche, Anrufliste, mehrere Konsolen.

## Offen vor dem Bau
- Öffnen über die API einmal testen (Einmal-Token mit `edit:space`).
- Protect-Schlüssel für die App unter einem eigenen UniFi-OS-Benutzer mit möglichst schmaler Rolle anlegen. Welche
  Rolle für Stream und Talkback reicht, wird getestet.
- Einrichtung der Tokens auf den PCs: wie sie in den Dienst kommen, ohne im Klartext in Intune zu stehen.
