# Betrieb

Für Admins: Bedienung (auch als Grundlage für die Einführung bei den Benutzern), Testen, jährliche Erneuerung der
Zugänge, Störungen. Installation in [installation.md](installation.md), Verteilung in [intune.md](intune.md).

## Bedienung
Klingel-Symbol unten rechts, eventuell unter dem Pfeil ^ in der Taskleiste.
- **Linksklick:** Status, die Türen mit ihrer Einstellung fürs Klingeln (Klick öffnet das Livebild), „Klingel
  pausieren“ (15 / 30 / 60 / 120 Minuten) und „Einstellungen …“, das dasselbe Menü wie der Rechtsklick öffnet.
- **Rechtsklick:** Version, Status, jede Tür mit ihrer Einstellung fürs Klingeln, „Klingel pausieren“, „Ton der Tür(en) automatisch einschalten“, Testklingeln, Prüfen, Beenden.

Beim Klingeln erscheint ein Fenster über allen anderen, mit Klingelton, Livebild und Knopf „Öffnen“. Nimmt jemand
anders ab (Mobile-App, anderer PC), schliesst es sich überall.

**Klingeln pro Tür** (Rechtsklick oder Linksklick → Einstellungen …, dann Tür → Auswahl; gilt für den angemeldeten
Benutzer): je Tür „sofort“ (Standard), „nach 10 s“ oder „aus“. Die Einstellung steht im Menü bei der Tür, z. B.
„Tür 2: aus“. Bei „nach 10 s“ blinkt zuerst das Symbol, nach 10 s erscheinen Fenster und Klingelton. Nimmt in dieser Zeit
jemand anders ab oder bricht der Besucher ab, erscheint nichts. Mit Klick aufs Symbol → „Jetzt anzeigen“ kommt das
Fenster sofort. Bei „aus“ erscheint für diese Tür nichts, das Livebild ohne Klingeln bleibt im Menü. Rufe, die so
nicht gezeigt wurden, gelten nicht als verpasst. Abweichungen stehen auch im Text am Symbol (Maus darauf). Beispiel:
Am Empfang klingelt die Haupttür sofort, eine Nebentür nach 10 s. Das ist eine Bequemlichkeit, keine Sperre. Soll eine
Tür auf einem PC gar nicht verfügbar sein (auch nicht zum Öffnen), gehört sie in `Doors` der sientrydesk.json
([installation.md](installation.md)).

„Ton an“ im Klingelfenster schaltet den Ton der Tür ein und den Klingelton aus. Nach dem Ende eines Rufs bleibt das
Fenster 10 s offen. Hat der Besucher abgebrochen (z. B. zweimal gedrückt) oder niemand abgenommen, lässt sich in
dieser Zeit noch öffnen.

**Gegensprechen:** „Sprechen – gedrückt halten“ drücken und halten, sprechen, loslassen, um die Antwort zu hören.
Man kann sofort beim Drücken losreden, nichts geht verloren, bis die Verbindung zur Tür steht. Nach dem Loslassen nimmt
das Mikrofon noch 0,6 s auf, damit das letzte Wort nicht abgeschnitten wird. Die Antwort kommt etwa 1 s verzögert,
also nach dem Loslassen kurz warten. Solange gesprochen wird, ist der Ton der Tür am PC stumm, sonst gäbe es Echo.
Nach 60 s am Stück endet das Sprechen von selbst. Möglich während des Rufs und in den 10 s danach, nicht wenn jemand anders angenommen oder
abgelehnt hat. Die PCs stimmen sich nicht ab: Sprechen zwei gleichzeitig, kommen beide Stimmen an der Tür an. Wer
spricht, hört die andere Stimme beim Loslassen über den Ton der Tür. Die Mobile-Apps klingeln weiter, bis jemand
öffnet oder der Ruf endet. Während einer Teams-Besprechung kann Windows andere Töne dämpfen (Sound → Weitere
Soundeinstellungen → Kommunikation).

| Symbol | Bedeutung |
|---|---|
| grün | bereit |
| blau | Klingel pausiert, auf diesem PC kein Fenster und kein Ton. Verpasste Rufe stehen im Menü |
| grau | Konsole nicht erreichbar, z. B. ausserhalb des Firmennetzes ohne VPN. Keine Meldung |
| orange | Klingeln geht, aber etwas anderes nicht (Livebild) oder die Zugänge laufen in 14 Tagen ab. Dann erscheint auch einmal pro Tag eine Meldung |
| rot | **dieser PC klingelt nicht** (Dienst, Token, Zertifikat). Nach 30 s erscheint eine Meldung |

## Testen nach der Installation
1. **Symbol grün.**
2. **Testklingeln** (Rechtsklick): nur auf diesem PC, ohne Livebild, Öffnen gesperrt. Steht eine Tür auf „nach 10 s“,
   blinkt zuerst das Symbol.
3. **Livebild ohne Klingeln:** Linksklick → Tür wählen. Ohne `DoorCameras` in der Konfiguration erscheint eine Tür
   erst nach ihrem ersten Klingeln.
4. **Echtes Klingeln:** Fenster, Klingelton, nach 1–2 s das Livebild.
5. **Am Handy abnehmen:** Das Fenster schliesst sich mit „Anderswo angenommen“.
6. **Öffnen am PC**, nur mit jemandem an der Tür. Im Access-Protokoll steht „<Benutzer> via SI EntryDesk (<PC>)“.
7. **Pause:** 15 Minuten pausieren, klingeln lassen. Es erscheint nichts, danach steht der Ruf im Menü.
8. **Gegensprechen**, nur mit jemandem an der Tür: Sprechtaste halten, sprechen, loslassen, Antwort hören. Einmal mit
   Lautsprechern, einmal mit Headset.

## Zugänge jährlich erneuern
Ohne Unterbruch, weil der alte Zugang bis zum Schluss gültig bleibt. Per Intune: [intune.md](intune.md#zugänge-jährlich-erneuern).
Von Hand:
1. Neuen Access-Token und neuen Protect-Schlüssel anlegen ([Rechte wie bei der Installation](installation.md#zugänge-anlegen),
   1 Jahr) und im Passwort-Manager ablegen.
2. Auf jedem PC: `powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"`.
3. In der sientrydesk.json die neuen Ablaufdaten eintragen und mit `install.ps1` verteilen.
4. Prüfen: Symbol grün, Livebild aus dem Menü, einmal klingeln.
5. Erst dann den alten Token und den alten Schlüssel löschen.
6. Im Betriebshandbuch der Anlage vermerken.

## Störungen
**Zuerst prüfen:** Rechtsklick auf das Symbol → „Prüfen …“ oder `SIEntryDesk.exe --check`. Das Fenster zeigt je
Voraussetzung grün, orange oder rot, mit einem Satz, was zu tun ist: Dienst, Autostart, App, Tonausgabe, Mikrofon,
Verbindung zum Dienst und Version auf dem PC; Konfiguration (mit Datum der sientrydesk.json, mit der der Dienst läuft),
Zugänge, Access und Protect (erreichbar, Zertifikat, Token und Rechte, Schlüssel), Klingel-Ereignisse, Stream pro Tür,
Gegensprechen und Ablaufdaten für die Anlage. Der Dienst liest die sientrydesk.json nur beim Start. Ist sie seither
geändert worden, wird die Zeile orange: Dienst oder PC neu starten. Ist die Konsole nicht erreichbar, steht nach
höchstens etwa 10 s eine Zeile „Konsole: nicht erreichbar“. Es braucht
keine Adminrechte und keine Eingaben. „Ergebnis kopieren“ legt den Text ohne Geheimnisse in die Zwischenablage, für
ein Ticket oder eine Mail. Ob der Ton beim Gegensprechen an der Tür ankommt, kann die Prüfung nicht feststellen.

| Symbol | Was tun |
|---|---|
| grau | Ausserhalb des Firmennetzes normal. Im Büro: Netz oder VPN, Ports 12445, 443, 7441 prüfen |
| orange | Text im Menü lesen. Protect-Schlüssel prüfen oder Zugänge erneuern |
| rot | Text im Menü und Protokoll lesen: Dienst läuft nicht, Token abgelehnt, Zertifikat passt nicht zum Pin |

| Meldung beim Sprechen | Was tun |
|---|---|
| kein Knopf „Sprechen“ | Gegensprechen auf diesem PC abgeschaltet (`Talkback`), Protect-Schlüssel fehlt oder die Kamera der Tür ist unbekannt |
| „Kein Zugriff aufs Mikrofon“ | Windows: Datenschutz → Mikrofon → Desktop-Apps zulassen |
| „Kein Mikrofon gefunden“ | Mikrofon oder Headset anschliessen, in Windows als Standardgerät für Kommunikation festlegen |
| Taste geht, an der Tür kommt nichts an | UDP 7004 vom PC zur Türstation blockiert; im Protokoll steht die Adresse der Türstation |
| „Protect-Schlüssel abgelehnt“ / „Protect nicht erreichbar“ | wie beim Livebild |

**Livebild schwarz oder stumm:** Das Protokoll des Videoplayers liegt beim Benutzer unter
`%LOCALAPPDATA%\SIEntryDesk\vlc.log`. „no … module matched“ heisst, dass ein Teil des Players fehlt.

**Protokoll des Dienstes** (nur für Administratoren lesbar): `C:\ProgramData\SIEntryDesk\logs\service-JJJJMMTT.log`.
Es enthält Türnamen, aber keine Tokens und keine Stream-Adressen. Installation über Intune:
`C:\Windows\Temp\SIEntryDesk-install.log`, weiteres in [intune.md](intune.md#kontrolle).

**Konfiguration prüfen:** `SIEntryDesk.exe --setup` mit „Installierte Konfiguration laden“, siehe
[installation.md](installation.md#mit-dem-einrichtungsassistenten).

## Sicherheit und Grenzen
- **Die Berechtigungen aus UniFi Access gelten nicht.** Wer an einem PC mit SI EntryDesk angemeldet ist, kann während
  eines Rufs öffnen. Zugriffskontrolle ist die Auswahl der PCs, siehe
  [installation.md](installation.md#wer-öffnen-darf-der-pc-nicht-die-person).
- Wer auf einem PC **lokaler Administrator** ist, kann die Zugänge aus `secrets.dat` auslesen und damit alle Türen
  öffnen. Standardbenutzer können das nicht. Deshalb: keine lokalen Adminrechte für Benutzer, BitLocker, LAPS.
- Die App öffnet nur während eines Rufs oder bis 10 s danach (nach „Besucher hat abgebrochen“ oder „Niemand hat
  abgenommen“). Das erzwingt der Dienst. Wer einen Token ausserhalb der App verwendet, ist daran nicht gebunden.
- Jede Öffnung steht im Access-Protokoll mit Benutzer und PC, jeder Livebild-Abruf im Protokoll des Dienstes.
- Das Livebild läuft nur über die geprüfte TLS-Verbindung des Dienstes. Die App bekommt eine Einmal-Adresse auf
  127.0.0.1, die mit dem Ruf bzw. nach der eingestellten Dauer endet.
