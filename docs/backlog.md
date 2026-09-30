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

## Nächste Schritte
- **0.4: Einrichtungsassistent** für Admins: Erreichbarkeit, Pins bestätigen, Tokens und Rechte prüfen (lesend und
  mit der Probe „Öffnen einer Tür, die es nicht gibt“), Protect-Schlüssel, Türen den Kameras zuordnen, RTSPS anlegen,
  Livebild testen, sientrydesk.json speichern.
- **Intune-Paketierung:** Win32-App an Gerätegruppe, Erkennung über die Version, Tokens getrennt verteilen.
- **Ton der Tür an einem echten PC prüfen:** erledigt am 2026-09-30, Ton hörbar. Gegensprechen geht an der Anlage
  derzeit auch in der Mobile-App nicht, das Problem liegt bei UniFi Access.

## Ideen, abhängig von Prüfungen
- Access schwieg in einer Aufzeichnung vom Mac zweimal 15 s, während Protect verbunden blieb. Bestätigt das
  Dienstprotokoll auf Windows solche Lücken, dann Protect-Klingeln als Ersatz nutzen, solange Access getrennt ist
  (Fenster ja, „anderswo angenommen“ und Öffnen nur mit Access).
- Nach der Installation von 0.2.4 kam das erste Livebild erst nach einiger Zeit, nach einem Neustart des Dienstes
  sofort. Ursache unklar, beobachten.
