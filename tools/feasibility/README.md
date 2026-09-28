# Machbarkeitstest

Ein Skript, `sied.py`, prüft gegen eine echte UniFi-Konsole, was die offiziellen APIs für SI EntryDesk hergeben.

## Voraussetzungen
- VPN bzw. Netzwerkzugang zur Konsole
- 1Password-CLI (`op`) mit aktivierter App-Integration, ffmpeg (`ffprobe`, `ffplay`)
- Python-Umgebung im Projektordner:
  ```bash
  python3 -m venv .venv && .venv/bin/pip install -r tools/feasibility/requirements.txt
  ```
- `tools/feasibility/sied.env` aus `sied.env.example` anlegen. Die Datei bleibt lokal.

## Zugänge
| Zugang | Wo anlegen | Rechte |
|---|---|---|
| Access-API-Token | Access → Settings → General → Advanced → API Token | `view:device` (Ereignisse, Geräte), `view:space` (Türen). `edit:space` erst für den Öffnen-Test |
| Protect-API-Schlüssel | UniFi OS → Settings → Control Plane → Integrations | Rechte des Kontos, unter dem er angelegt wird |

## Aufruf
Alle Befehle aus dem Projektordner, mit dem 1Password-Konto, in dem der Eintrag liegt:
```bash
op run --account <konto> --env-file tools/feasibility/sied.env -- .venv/bin/python tools/feasibility/sied.py <befehl>
```

| Befehl | Wirkung | Ändert etwas? |
|---|---|---|
| `cert` | Fingerprints für Port 443, 12445, 7441 anzeigen. Einmal prüfen, dann in `sied.env` eintragen | nein |
| `inventory` | Access-Geräte und -Türen, Protect-Version und -Kameras mit Mikrofon/Lautsprecher | nein |
| `listen [--minutes N] [--verbose]` | Klingel-Ereignisse von Access und Protect parallel mitschreiben, mit Verzögerung | nein |
| `stream <kamera-id> [--play]` | RTSPS-Stream prüfen: Codecs, Auflösung, Ton | nein |
| `stream <kamera-id> --create` / `--delete` | RTSPS-Freigabe der Kamera anlegen bzw. entfernen | **ja** |
| `talkback <kamera-id>` | Talkback-Sitzung anlegen, Audioformat anzeigen | nein |
| `talkback <kamera-id> --tone` | kurzen, leisen Ton an der Türstation abspielen | **hörbar an der Tür** |
| `unlock <tür-id> --ja-tuer-oeffnen` | Tür öffnen, erscheint im Access-Protokoll als „SI EntryDesk Test“ | **öffnet die Tür** |

## Sicherheit
- Token und Schlüssel nur über `op run`, nie in Dateien oder auf der Kommandozeile.
- Jede Verbindung wird gegen den gepinnten Fingerprint geprüft. Stimmt er nicht, bricht das Skript ab.
- ffmpeg prüft Zertifikate ab Version 9 selbst. Das Skript übergibt das gepinnte Zertifikat als einzigen Vertrauensanker
  (`-ca_file`) und den Namen aus dem Zertifikat (`-verifyhost`), weil die Konsole per IP angesprochen wird.
  Für die App muss der Videoplayer genauso pinnen.
- Protokolle landen in `tools/feasibility/out/`. Tokens und Stream-Pfade sind darin unkenntlich, Tür- und
  Gerätenamen nicht. Der Ordner wird nicht eingecheckt.
