# SIEntryDesk

Windows-11-App von Schaniel Informatik für Türsprechstellen mit UniFi Protect und UniFi Access.
Nicht mit Ubiquiti verbunden.

## Ziel
Wenn es an der Tür klingelt, springt auf den gewünschten PCs sofort ein Fenster in den Vordergrund:
- Klingelton und deutlich sichtbares Popup, auch wenn der PC gerade anderweitig benutzt wird
- Livebild der Türstation
- Gegensprechen (Hören und Sprechen), sofern die offiziellen APIs das hergeben
- grosser Knopf „Öffnen“
- Nimmt jemand anders ab (Mobile-App, anderer PC), schliesst sich das Fenster überall

Hintergrund: Die UniFi-Web-App unter Windows braucht dafür viele Klicks und benachrichtigt nur schwach.
Auf dem Handy funktioniert es gut, am PC nicht.

## Testhardware
- Türstationen: **G6 Pro Entry** und **G6 Entry**, beide in UniFi Protect
- Türsteuerung: je ein **UA Hub Door** in UniFi Access
- Protect und Access laufen auf derselben UniFi-OS-Konsole

## Schnittstellen (nur offizielle APIs)
- **UniFi Access Developer API:** REST und WebSocket, Port 12445, API-Token mit Scopes.
  WebSocket-Ereignisse `access.remote_view` (Klingeln) und `access.remote_view.change` (abgenommen/beendet),
  REST-Aufruf zum Öffnen der Tür.
- **UniFi Protect Integration API:** API-Schlüssel aus UniFi OS (Integrations).
  Ring-Ereignisse, Videostream (RTSPS) und möglicherweise eine Talkback-Sitzung für Gegensprechen.
- Keine inoffiziellen oder undokumentierten Endpunkte. Wenn eine Funktion nur so ginge, ist sie Stufe 2 und
  wird vorher mit Marcel besprochen.

## Vorgehen
1. **Machbarkeitstest** vom Mac aus, VPN zur Testumgebung. Zu prüfen:
   - Kommt das Klingel-Ereignis per WebSocket an (Access und/oder Protect)?
   - Ist der Videostream der G6 Entry erreichbar, und mit welcher Verzögerung?
   - Lässt sich eine Talkback-Sitzung für die G6 Entry aufbauen, mit welchem Codec?
   - Öffnet der Unlock-Aufruf die Tür (nur mit Marcels Freigabe und jemandem an der Tür)?
   Ergebnis als kurzes Protokoll festhalten, dann den MVP-Umfang festlegen.
2. **MVP:** C#/.NET (aktuelle LTS), WPF, Tray-App, Video über LibVLCSharp.
   Popup „immer im Vordergrund“, Toast-Benachrichtigung, Klingelton, Knopf „Öffnen“.
   Fallback für Gegensprechen: Knopf, der die Access-Web-App direkt an der richtigen Stelle öffnet.
3. **Stufe 2:** Gegensprechen in der App, falls die API es zulässt.
4. **Verteilung:** Win32-App über Intune an ausgewählte PCs oder Gruppen.

## Arbeitsweise
- Entwickelt wird auf dem Mac. Das .NET SDK baut Windows-Apps auch dort (`EnableWindowsTargeting`),
  ausführen und testen kann man sie aber nur unter Windows. **Marcel testet auf Windows 11.**
- Vor jedem Einrichtungsschritt prüfen, ob das Werkzeug mit nicht-interaktiver Bash funktioniert.
- **Sprache:** Kommentare in Code, Skripten und Projektdateien auf Englisch (Entscheid 2026-10-27, Repository ist
  öffentlich). `docs/entwicklung` bleibt Deutsch. Ab 0.10.0 (Entscheid 2026-11-11) gibt es die Oberfläche für
  Benutzer auf Deutsch, Englisch und Polnisch (`Language` in der sientrydesk.json), Prüfen, Protokoll und
  Einrichtungsassistent auf Deutsch oder Englisch, Meldungen und Hilfe der Skripte nur auf Englisch, Admin-Doku in
  `docs/admin` (Deutsch) und `docs/admin/en` (Englisch). Jeder Text der Programme steht als `Tr.User(de, en, pl)`
  oder `Tr.Admin(de, en)` direkt an seiner Stelle, nur mit String-Literalen. `python3 tools/texts.py <datei>.xlsx`
  zieht alle Texte für das Gegenlesen in eine Tabelle, `publish.sh` prüft mit `--check`, dass jeder lesbar ist.
  Neue Texte immer in allen Sprachen, Polnisch mit `Tr.PlPlural` bei Zahlen.
- Was Admins für Einrichtung und Betrieb wissen müssen, gehört in `docs/admin/` (`installation.md`, `intune.md`,
  `betrieb.md`), nicht nur in den Chat. Der Build legt diese Dateien als `Doku\` ins Paket.
- Ablage: `deploy/` nur ausgelieferte Dateien (`programm/`, `tokens/`), keine Anleitungen.
  `docs/entwicklung/` für Unterlagen zur Weiterentwicklung (Machbarkeitstest, API, Architektur, Backlog).

## Sicherheit
Die App kann eine Tür öffnen. Der Code wird später öffentlich, deshalb gilt von Anfang an:
- **Tokens und Schlüssel nie in Dateien, Code, Logs oder im Repo.** Für die Entwicklung als Umgebungsvariablen
  oder aus 1Password (`op`). In der App im Windows-Anmeldeinformationsspeicher (Credential Manager/DPAPI).
- Token mit **minimalen Scopes**.
- Zertifikat der UniFi-Konsole **pinnen** (Fingerprint in der Konfiguration), statt die TLS-Prüfung abzuschalten.
- Keine Telemetrie. Jede Türöffnung ist im Access-Protokoll nachvollziehbar.
- Eingaben aus Ereignissen und API-Antworten als nicht vertrauenswürdig behandeln.

## Repository und Veröffentlichung
- Name: Repository und Ordner `sientrydesk`, Anzeigename „SI EntryDesk“, Namespace und Programmdatei `SIEntryDesk`.
- **Zuerst privat.** Öffentlich erst, wenn die App in der Testumgebung stabil läuft und der Code gemeinsam
  durchgesehen ist.
- Lizenz MIT, „as is“. README mit getesteter Hardware und dem Hinweis „not affiliated with Ubiquiti“.
  `SECURITY.md` für die Meldung von Schwachstellen.
- **Keine Kundendaten im Repo und in dieser Datei:** keine Firmen- oder Türnamen, IP-Adressen, Geräte-IDs,
  Tokens oder Screenshots mit solchen Angaben. Konfiguration nur lokal, im Repo nur ein Beispiel mit Platzhaltern.
- **Vor jedem Push** den Diff auf sensible Inhalte prüfen und Marcels Freigabe einholen.
- Git-Identität: Marcels normale Identität, nicht die kundenspezifische aus einem Kundenordner.
- Keine Code-Signierung (Entscheid 2026-10-03): SmartScreen warnt bei Downloads, das README erklärt es.
  Für die Verteilung über Intune ist keine Signatur nötig.
