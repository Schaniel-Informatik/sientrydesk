# Offene Punkte

In 0.3.0 umgesetzt: Programm-Icon, Linksklick-Menü, Kameranamen aus Protect, Statusfarben (rot mit Meldung nach 30 s,
grau ohne Meldung ausserhalb des Firmennetzes), Klingel pausieren, Livebild ohne Klingeln 60 s, „Ton an“ stoppt den
Klingelton, „Ton der Tür automatisch einschalten“ pro Benutzer, Konfigurationsdatei statt Installer-Parameter,
`set-tokens.ps1`, `show-pins`, Warnung vor ablaufenden Schlüsseln.

## Für den nächsten Build umgesetzt (noch nicht gebaut)
- Erinnerung vor Ablauf: ab 14 Tagen einmal pro Tag eine Meldung auf jeder App, Symbol erst dann orange.
- Fenster nach Rufende 10 s offen, Livebild läuft weiter. In dieser Zeit noch öffnen nach 108 (Besucher hat
  abgebrochen) und 105 (niemand hat abgenommen), nicht nach 400 und 106. Kein zweites Öffnen, wenn schon geöffnet.
- Öffnung ohne Namen („N/A“) als „Tür geöffnet“.
- Anleitung: ein Access-Token (Gerät = Anzeigen, Standorte = Bearbeiten). Protect-Schlüssel unter Marcels Konto
  (Variante A, Risiko zur Kenntnis genommen).

## Nächste Version (umgesetzt, noch nicht gebaut)
- Hilfe (`--help`) mit kürzeren Zeilen, das Meldungsfenster brach sie unschön um.

## 0.5.3 (2026-10-02 getestet: Update über Intune, alte Zugänge, detect.ps1, Hilfe; Öffnen steht noch aus)
- Ablage neu: `deploy/programm`, `deploy/zugaenge`, `docs/admin` (als `Doku\` im Paket), `docs/entwicklung`.
- Eigener Token nur zum Öffnen entfernt (Altlast aus dem Machbarkeitstest, brachte keine Sicherheit). `set-secrets`
  behält das Format mit leerer Zeile 2, damit ältere Pakete der Zugänge passen; ein Wert dort wird abgelehnt.
  `tokens.txt` kennt nur noch `access=` und `protect=`, `set-tokens.ps1` fragt zwei Werte ab.
- Abfragetexte in `set-tokens.ps1` und Hinweistext im Einrichtungsassistenten nachgeführt.

## 0.5.2 (gebaut, unter Windows noch zu testen)
- `prepare-intune.ps1` in beiden Paketen: prüft den Ordner, erzeugt `detect.ps1` bzw. prüft `tokens.txt`, ruft
  IntuneWinAppUtil auf und zeigt die Einstellungen für Intune. Das Paket der Zugänge löscht `tokens.txt` danach selbst.
- `detect.ps1` entsteht im Paketordner und wird mit `uninstall.ps1` und `set-tokens.ps1` nach
  `C:\Program Files\SIEntryDesk` installiert.
- `uninstall.ps1` entfernt auch Installationsprotokoll und Benutzereinstellungen.
- Neu anfangen in Intune: Apps gelöscht und neu angelegt (neue IDs, keine 24-h-Sperre aus den Fehlversuchen mit 0.5.0).

## 0.5.1
- Intune startet Installationsbefehle als 32-Bit-Prozess. 0.5.0 installierte deshalb nach `C:\Program Files (x86)`
  und in `WOW6432Node`, die Erkennung fand nichts (0x87D1041C). Die Skripte wechseln jetzt selbst in die
  64-Bit-PowerShell, `install.ps1` setzt den Dienstpfad immer neu und räumt Reste der 32-Bit-Installation weg.
- Erkennungsskript pro Paket: Dienst, Version, SHA-256 der sientrydesk.json.
- Installationsprotokoll unter Intune: `C:\Windows\Temp\SIEntryDesk-install.log`.
- Assistent: Ergebnis der Fingerabdrücke nach dem Laden gleich oben.

## 0.5.0
- Zwei getrennte Pakete: Programm (`SIEntryDesk-<Version>-win-x64.zip`) und Zugänge (`SIEntryDesk-Zugaenge-<Version>.zip`).
- `tokens.txt` mit benannten Zeilen (`access=`, `protect=`), Prüfung mit Zeilennummer, ohne den Inhalt zu nennen.
- Einrichtungsassistent lädt eine bestehende sientrydesk.json (installierte, andere Datei oder `--setup <Datei>`)
  und zeigt Abweichungen: Pins, Ablaufdaten, Türen, Kameras.
- `--help` für App und Dienst. Der Dienst startet bei unbekannten Argumenten nicht mehr.

## Nächste Schritte
- **0.4 Einrichtungsassistent:** umgesetzt (`SIEntryDesk.exe --setup`). Unter Windows noch zu testen.
- **Intune-Paketierung:** 0.5.0 bereit, erster Durchlauf mit einem Gerät steht aus.
- **Gegensprechen (Stufe 2):** Die Anlage kann es wieder (Access-Web-App und Mobile, 2026-09-30). Grundlagen,
  Grenzen und offene Entscheide in [gegensprechen.md](gegensprechen.md).
- **Ton der Tür an einem echten PC prüfen:** erledigt am 2026-09-30, Ton hörbar. Gegensprechen geht an der Anlage
  derzeit auch in der Mobile-App nicht, das Problem liegt bei UniFi Access.

## Ideen, abhängig von Prüfungen
- Access schwieg in einer Aufzeichnung vom Mac zweimal 15 s, während Protect verbunden blieb. Bestätigt das
  Dienstprotokoll auf Windows solche Lücken, dann Protect-Klingeln als Ersatz nutzen, solange Access getrennt ist
  (Fenster ja, „anderswo angenommen“ und Öffnen nur mit Access).
- Nach der Installation von 0.2.4 kam das erste Livebild erst nach einiger Zeit, nach einem Neustart des Dienstes
  sofort. Ursache unklar, beobachten.
