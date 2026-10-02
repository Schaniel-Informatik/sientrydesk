# SI EntryDesk

Windows-App von Schaniel Informatik für Türsprechstellen mit UniFi Protect und UniFi Access. Wenn es klingelt,
erscheint auf den gewünschten PCs sofort ein Fenster mit Klingelton, Livebild und dem Knopf „Öffnen“. Nimmt jemand
anders ab, schliesst es sich überall.

**Not affiliated with Ubiquiti.** Nutzt ausschliesslich die offiziellen Schnittstellen (Access Developer API,
Protect Integration API).

Stand: Pilot, Repository privat.

## Wo was steht
**Für Admins** (`docs/admin/`, kommt auch als `Doku\` ins Paket):

| Datei | Inhalt |
|---|---|
| [installation.md](docs/admin/installation.md) | Voraussetzungen, Zugänge anlegen, sientrydesk.json, Installation von Hand, wo was liegt und wo die Zugänge gespeichert sind |
| [intune.md](docs/admin/intune.md) | Verteilung mit Intune: zwei Win32-Apps (Programm und Zugänge), `prepare-intune.ps1`, Erkennung, Updates, Kontrolle |
| [betrieb.md](docs/admin/betrieb.md) | Bedienung, Testen, jährliche Erneuerung der Zugänge, Störungen, Sicherheit |

**Für die Entwicklung** (`docs/entwicklung/`):

| Datei | Inhalt |
|---|---|
| [machbarkeitstest.md](docs/entwicklung/machbarkeitstest.md) | Was an einer echten Anlage getestet wurde, mit Ergebnissen |
| [api-notizen.md](docs/entwicklung/api-notizen.md) | Verwendete API-Endpunkte, Ereignisse und im Test ermitteltes Verhalten |
| [architektur.md](docs/entwicklung/architektur.md) | Architektur und Entscheide (Dienst und App getrennt, Livebild über den Dienst) |
| [gegensprechen.md](docs/entwicklung/gegensprechen.md) | Gegensprechen: wie es geht, Grenzen, Entscheide |
| [backlog.md](docs/entwicklung/backlog.md) | Offene Punkte und nächste Schritte |
| [vorwissen.md](docs/entwicklung/vorwissen.md) | Hintergrund aus der Praxis |

**Code und Pakete:**

| Pfad | Inhalt |
|---|---|
| [deploy/programm/](deploy/programm/) | Was ins Programmpaket kommt: `install.ps1`, `uninstall.ps1`, `set-tokens.ps1`, `prepare-intune.ps1`, Beispiel-Konfiguration |
| [deploy/zugaenge/](deploy/zugaenge/) | Was ins Paket der Zugänge für Intune kommt: `set-tokens-intune.ps1`, `prepare-intune.ps1`, `tokens.example.txt` |
| `src/` | Quellcode: `SIEntryDesk.Core` (gemeinsame Logik), `.Service` (Windows-Dienst), `.App` (Tray-App) |
| `tests/` | Automatische Tests der gemeinsamen Logik |
| `tools/` | Entwicklerwerkzeuge (Machbarkeitstest, DevCli für den Mac, `intune-check.ps1` für Intune über Graph) |
| `build/` | Build-Skripte (`publish.sh` erzeugt das Windows-Paket) |

## Bauen
Auf dem Mac oder unter Windows mit dem .NET SDK 10: `./build/publish.sh` testet und erzeugt zwei getrennte Pakete:
`artifacts/SIEntryDesk-<Version>-win-x64.zip` (Programm) und `artifacts/SIEntryDesk-Zugaenge-<Version>.zip` (Skript
und Vorlage für die Zugänge per Intune).
