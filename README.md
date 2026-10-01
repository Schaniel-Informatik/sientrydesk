# SI EntryDesk

Windows-App von Schaniel Informatik für Türsprechstellen mit UniFi Protect und UniFi Access. Wenn es klingelt,
erscheint auf den gewünschten PCs sofort ein Fenster mit Klingelton, Livebild und dem Knopf „Öffnen“. Nimmt jemand
anders ab, schliesst es sich überall.

**Not affiliated with Ubiquiti.** Nutzt ausschliesslich die offiziellen Schnittstellen (Access Developer API,
Protect Integration API).

Stand: Pilot, Repository privat.

## Wo was steht
| Pfad | Inhalt |
|---|---|
| [docs/betrieb.md](docs/betrieb.md) | **Einrichtung und Betrieb:** Zugänge und Rechte, Voraussetzungen bei UniFi, Konfiguration, Installation, jährliche Erneuerung, Störungen |
| [docs/intune.md](docs/intune.md) | **Verteilung mit Intune:** zwei Win32-Apps (Programm und Zugänge), Befehle, Erkennung, Updates, jährliche Erneuerung |
| [deploy/](deploy/) | Installationsskripte, Beispiel-Konfiguration, Kurzanleitung (`PILOT.md`); in `intune-zugaenge/` das eigene Paket für die Zugänge |
| [docs/machbarkeitstest.md](docs/machbarkeitstest.md) | Was an einer echten Anlage getestet wurde, mit Ergebnissen |
| [docs/api-notizen.md](docs/api-notizen.md) | Verwendete API-Endpunkte, Ereignisse und im Test ermitteltes Verhalten |
| [docs/mvp.md](docs/mvp.md) | Architektur und Entscheide (Dienst und App getrennt, Livebild über den Dienst) |
| [docs/gegensprechen.md](docs/gegensprechen.md) | Gegensprechen: wie es geht, Grenzen, Entscheide |
| [docs/backlog.md](docs/backlog.md) | Offene Punkte und nächste Schritte |
| [docs/vorwissen.md](docs/vorwissen.md) | Hintergrund aus der Praxis |
| `src/` | Quellcode: `SIEntryDesk.Core` (gemeinsame Logik), `.Service` (Windows-Dienst), `.App` (Tray-App) |
| `tests/` | Automatische Tests der gemeinsamen Logik |
| `tools/` | Entwicklerwerkzeuge (Machbarkeitstest, DevCli für den Mac) |
| `build/` | Build-Skripte (`publish.sh` erzeugt das Windows-Paket) |

## Bauen
Auf dem Mac oder unter Windows mit dem .NET SDK 10: `./build/publish.sh` testet und erzeugt zwei getrennte Pakete:
`artifacts/SIEntryDesk-<Version>-win-x64.zip` (Programm) und `artifacts/SIEntryDesk-Zugaenge-<Version>.zip` (Skript
und Vorlage für die Zugänge per Intune).
