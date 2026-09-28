#!/usr/bin/env python3
"""SI EntryDesk – Machbarkeitstest gegen UniFi Access und UniFi Protect.

Nur offizielle APIs:
  - UniFi Access Developer API (Port 12445, Bearer-Token)
  - UniFi Protect Integration API (UniFi OS, Header X-API-KEY)

Zugangsdaten kommen ausschliesslich aus Umgebungsvariablen, gesetzt über `op run`.
TLS wird gegen gepinnte SHA-256-Fingerprints geprüft, die Prüfung wird nie abgeschaltet.
Aufruf und Variablen: siehe README.md in diesem Ordner.
"""
import argparse
import asyncio
import hashlib
import json
import os
import re
import shutil
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from urllib.parse import quote, urlsplit, urlunsplit

ACCESS_PORT = 12445
PROTECT_PORT = 443
RTSPS_PORT = 7441
ACCESS_BASE = "/api/v1/developer"
PROTECT_BASE = "/proxy/protect/integration/v1"
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")

# reason_code aus access.remote_view.change (Access API Reference, Abschnitt 11.1)
REMOTE_VIEW_REASONS = {
    105: "Zeitüberschreitung, niemand hat abgenommen",
    106: "Öffnen abgelehnt",
    107: "Tür geöffnet",
    108: "Besucher hat abgebrochen",
    400: "anderswo angenommen",
}

ID_PATTERN = re.compile(r"^[A-Za-z0-9-]{6,64}$")
CONTROL_CHARS = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]")
SECRET_KEYS = {"token", "access_token", "api_key", "apikey", "password", "secret"}


# ---------------------------------------------------------------- Hilfsfunktionen

def die(msg):
    print(f"FEHLER: {msg}", file=sys.stderr, flush=True)
    sys.exit(2)


def env(name):
    value = os.environ.get(name, "").strip()
    if not value:
        die(f"Umgebungsvariable {name} fehlt. Aufruf über `op run --env-file ...`, siehe README.md.")
    return value


def clean(text):
    """Steuerzeichen aus nicht vertrauenswürdigem Text entfernen (keine Terminal-Escape-Sequenzen)."""
    return CONTROL_CHARS.sub("?", str(text))


def mask_url(url):
    parts = urlsplit(url)
    return urlunsplit((parts.scheme, parts.netloc, "/***", "", ""))


def redact(obj):
    """Tokens und Stream-Pfade vor Ausgabe und Log unkenntlich machen."""
    if isinstance(obj, dict):
        return {k: ("***" if k.lower() in SECRET_KEYS and v else redact(v)) for k, v in obj.items()}
    if isinstance(obj, list):
        return [redact(v) for v in obj]
    if isinstance(obj, str) and obj.startswith(("rtsps://", "rtsp://")):
        return mask_url(obj)
    return obj


def check_id(value, what):
    if not ID_PATTERN.match(value):
        die(f"Ungültige {what}: {clean(value)!r}")
    return value


def now():
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def show(obj):
    print(clean(json.dumps(redact(obj), indent=2, ensure_ascii=False)), flush=True)


# ---------------------------------------------------------------- TLS mit Pinning

def fetch_cert(host, port, timeout=5):
    """Zertifikat abholen, ohne ihm zu vertrauen. Nur zum Vergleich mit dem Pin."""
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    with socket.create_connection((host, port), timeout=timeout) as sock:
        with ctx.wrap_socket(sock, server_hostname=host) as tls:
            return tls.getpeercert(binary_form=True)


def fingerprint(der):
    return hashlib.sha256(der).hexdigest().upper()


def normalize_fp(value):
    return re.sub(r"[^0-9A-Fa-f]", "", value).upper()


def format_fp(fp):
    return ":".join(fp[i:i + 2] for i in range(0, len(fp), 2))


def check_pin(host, port, pin_var):
    pin = normalize_fp(env(pin_var))
    if len(pin) != 64:
        die(f"{pin_var} ist kein SHA-256-Fingerprint.")
    try:
        der = fetch_cert(host, port)
    except OSError as err:
        die(f"Konsole auf Port {port} nicht erreichbar ({clean(type(err).__name__)}). VPN aktiv?")
    got = fingerprint(der)
    if got != pin:
        die(f"Zertifikat auf Port {port} passt nicht zum Pin in {pin_var}. Verbindung abgebrochen.\n"
            f"  erwartet {format_fp(pin)}\n  erhalten {format_fp(got)}")
    return der


def cert_dns_name(der):
    """Ersten DNS-Namen aus dem Zertifikat lesen, für die Namensprüfung externer Werkzeuge."""
    res = subprocess.run(["openssl", "x509", "-inform", "DER", "-noout", "-ext", "subjectAltName"],
                         input=der, capture_output=True, timeout=10)
    match = re.search(r"DNS:([A-Za-z0-9.-]+)", res.stdout.decode(errors="replace"))
    if not match:
        die("Kein DNS-Name im Zertifikat, Namensprüfung für ffmpeg nicht möglich.")
    return match.group(1)


def pinned_context(host, port, pin_var):
    """SSL-Kontext, der ausschliesslich das gepinnte Zertifikat akzeptiert.

    Das Zertifikat der Konsole ist selbst ausgestellt. Es wird nach dem Fingerprint-Vergleich
    als einziger Vertrauensanker geladen, die eigentliche Verbindung prüft dann dagegen.
    Die Namensprüfung entfällt, weil der Pin sie ersetzt.
    """
    der = check_pin(host, port, pin_var)
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_REQUIRED
    ctx.verify_flags |= ssl.VERIFY_X509_PARTIAL_CHAIN
    ctx.load_verify_locations(cadata=der)
    return ctx


def http(method, url, ctx, headers, body=None, timeout=15):
    data = json.dumps(body).encode() if body is not None else None
    hdrs = {"Accept": "application/json", **headers}
    if data is not None:
        hdrs["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                         urllib.request.HTTPSHandler(context=ctx))
    try:
        with opener.open(req, timeout=timeout) as resp:
            status, raw = resp.status, resp.read()
    except urllib.error.HTTPError as err:
        status, raw = err.code, err.read()
    try:
        payload = json.loads(raw) if raw else None
    except ValueError:
        payload = {"_raw": clean(raw[:500].decode(errors="replace"))}
    return status, payload


class Access:
    def __init__(self, token_var="SIED_ACCESS_TOKEN"):
        self.host = env("SIED_HOST")
        self.token = env(token_var)
        self.ctx = pinned_context(self.host, ACCESS_PORT, "SIED_PIN_ACCESS")
        self.base = f"https://{self.host}:{ACCESS_PORT}{ACCESS_BASE}"
        self.ws_url = f"wss://{self.host}:{ACCESS_PORT}{ACCESS_BASE}/devices/notifications"

    @property
    def headers(self):
        return {"Authorization": f"Bearer {self.token}"}

    def call(self, method, path, body=None):
        return http(method, self.base + path, self.ctx, self.headers, body)


class Protect:
    def __init__(self):
        self.host = env("SIED_HOST")
        self.key = env("SIED_PROTECT_KEY")
        self.ctx = pinned_context(self.host, PROTECT_PORT, "SIED_PIN_PROTECT")
        self.base = f"https://{self.host}{PROTECT_BASE}"
        self.ws_url = f"wss://{self.host}{PROTECT_BASE}/subscribe/events"

    @property
    def headers(self):
        return {"X-API-KEY": self.key}

    def call(self, method, path, body=None):
        return http(method, self.base + path, self.ctx, self.headers, body)


def report_error(api, status, payload, hint=""):
    print(f"  {api}: HTTP {status}", flush=True)
    if payload:
        show(payload)
    if status in (401, 403) and hint:
        print(f"  Hinweis: {hint}", flush=True)


# ---------------------------------------------------------------- cert

def cmd_cert(_args):
    """Fingerprints der Konsole anzeigen. Einmal prüfen, dann als Pins eintragen."""
    host = env("SIED_HOST")
    for label, port, var in (("UniFi OS / Protect", PROTECT_PORT, "SIED_PIN_PROTECT"),
                             ("Access API", ACCESS_PORT, "SIED_PIN_ACCESS"),
                             ("Protect RTSPS", RTSPS_PORT, "SIED_PIN_RTSPS")):
        try:
            der = fetch_cert(host, port)
        except OSError as err:
            print(f"# {label}, Port {port}: nicht erreichbar ({clean(err)})")
            continue
        fp = fingerprint(der)
        details = ""
        if shutil.which("openssl"):
            res = subprocess.run(["openssl", "x509", "-inform", "DER", "-noout", "-subject", "-enddate"],
                                 input=der, capture_output=True, timeout=10)
            details = " | ".join(clean(line) for line in res.stdout.decode(errors="replace").splitlines())
        current = normalize_fp(os.environ.get(var, ""))
        state = "entspricht dem Pin" if current == fp else ("WEICHT VOM PIN AB" if current else "noch kein Pin")
        print(f"# {label}, Port {port}: {details} ({state})")
        print(f"{var}={format_fp(fp)}")


# ---------------------------------------------------------------- scopes

# Ein lesender Endpunkt je Recht (Access API Reference). Bearbeiten-Rechte lassen sich so nicht
# prüfen, ohne etwas zu ändern.
SCOPE_PROBES = (
    ("view:device", "/devices"),
    ("view:space", "/doors"),
    ("view:user", "/users?page_num=1&page_size=1"),
    ("view:visitor", "/visitors?page_num=1&page_size=1"),
    ("view:policy", "/access_policies"),
    ("view:credential", "/credentials/nfc_cards/tokens?page_num=1&page_size=1"),
    ("view:webhook", "/webhooks/endpoints"),
)


def cmd_scopes(_args):
    """Nur lesend: welche Leserechte haben die Access-Tokens? Zeigt nur den Status, keine Daten."""
    for var in ("SIED_ACCESS_TOKEN", "SIED_ACCESS_UNLOCK_TOKEN"):
        if not os.environ.get(var, "").strip():
            print(f"== {var}: nicht gesetzt")
            continue
        acc = Access(var)
        print(f"== {var}")
        for scope, path in SCOPE_PROBES:
            status, payload = acc.call("GET", path)
            code = payload.get("code") if isinstance(payload, dict) else None
            allowed = status == 200 and code == "SUCCESS"
            print(f"  {scope:16} {'ERLAUBT' if allowed else 'verweigert'}  (HTTP {status}, {clean(code)})", flush=True)


# ---------------------------------------------------------------- inventory

def flatten(items):
    for item in items or []:
        if isinstance(item, list):
            yield from flatten(item)
        else:
            yield item


def cmd_inventory(_args):
    """Nur lesend: Access-Geräte und -Türen, Protect-Version und -Kameras."""
    if os.environ.get("SIED_ACCESS_TOKEN", "").strip():
        inventory_access()
    else:
        print("== Access: übersprungen, SIED_ACCESS_TOKEN fehlt")
    if os.environ.get("SIED_PROTECT_KEY", "").strip():
        inventory_protect()
    else:
        print("== Protect: übersprungen, SIED_PROTECT_KEY fehlt")


def inventory_access():
    acc = Access()
    print("== Access: Geräte (view:device)")
    status, payload = acc.call("GET", "/devices")
    if status == 200 and payload and payload.get("code") == "SUCCESS":
        for dev in flatten(payload.get("data")):
            print(f"  {clean(dev.get('type', '?')):10} {clean(dev.get('name', '')):28} "
                  f"alias={clean(dev.get('alias', ''))!r} id={clean(dev.get('id', ''))}")
    else:
        report_error("Access /devices", status, payload, "Token braucht view:device")

    print("== Access: Türen (view:space)")
    status, payload = acc.call("GET", "/doors")
    if status == 200 and payload and payload.get("code") == "SUCCESS":
        for door in payload.get("data") or []:
            print(f"  {clean(door.get('full_name') or door.get('name', '')):40} "
                  f"relay={clean(door.get('door_lock_relay_status'))} "
                  f"position={clean(door.get('door_position_status'))} "
                  f"hub={door.get('is_bind_hub')} id={clean(door.get('id', ''))}")
    else:
        report_error("Access /doors", status, payload, "Token braucht view:space")


def inventory_protect():
    pro = Protect()
    print("== Protect: Version")
    status, payload = pro.call("GET", "/meta/info")
    if status == 200:
        print(f"  Protect {clean((payload or {}).get('applicationVersion'))}")
    else:
        report_error("Protect /meta/info", status, payload, "API-Schlüssel prüfen")

    print("== Protect: Kameras")
    status, payload = pro.call("GET", "/cameras")
    if status == 200:
        for cam in payload or []:
            flags = cam.get("featureFlags") or {}
            print(f"  {clean(cam.get('name', '')):28} state={clean(cam.get('state'))} "
                  f"mic={flags.get('hasMic')}/{cam.get('isMicEnabled')} speaker={flags.get('hasSpeaker')} "
                  f"lcd={'ja' if cam.get('lcdMessage') is not None else 'nein'} id={clean(cam.get('id', ''))}")
    else:
        report_error("Protect /cameras", status, payload)


# ---------------------------------------------------------------- listen

def log_line(fh, source, message):
    fh.write(json.dumps({"t": time.time(), "source": source, "msg": redact(message)}, ensure_ascii=False) + "\n")
    fh.flush()


def describe_access(msg):
    event = msg.get("event")
    data = msg.get("data") or {}
    if event == "access.remote_view":
        created = data.get("create_time")
        delay = f", Verzögerung ca. {time.time() - created:.1f} s" if isinstance(created, (int, float)) else ""
        return (f">>> KLINGELN (Access) Tür={clean(data.get('door_name'))!r} Gerät={clean(data.get('device_type'))} "
                f"request_id={clean(data.get('request_id'))}{delay}")
    if event == "access.remote_view.change":
        code = data.get("reason_code")
        reason = REMOTE_VIEW_REASONS.get(code, "unbekannt")
        return (f"<<< RUF BEENDET (Access) reason_code={clean(code)} ({reason}) "
                f"request_id={clean(data.get('remote_call_request_id'))}")
    if event == "access.data.device.remote_unlock":
        return f"*** TÜR PER FERNZUGRIFF GEÖFFNET (Access) Tür={clean(data.get('name'))!r}"
    if event in ("access.doorbell.incoming", "access.doorbell.completed"):
        return f">>> {clean(event)} (Access)"
    return None


def describe_protect(msg):
    item = msg.get("item") or {}
    if item.get("type") != "ring":
        return None
    start = item.get("start")
    if msg.get("type") == "add":
        delay = f", Verzögerung {time.time() * 1000 - start:.0f} ms" if isinstance(start, (int, float)) else ""
        return f">>> KLINGELN (Protect) Gerät={clean(item.get('device'))} event={clean(item.get('id'))}{delay}"
    return f"--- Ring aktualisiert (Protect) end={clean(item.get('end'))} event={clean(item.get('id'))}"


async def listen_ws(source, url, ctx, headers, describe, fh, verbose, idle_timeout=None):
    """WebSocket mitschreiben. Mit idle_timeout: keine Protokoll-Pings, sondern Neuaufbau,
    wenn so lange keine Nachricht kommt (die Access-API beantwortet keine Pings und sendet
    stattdessen alle 5 s ein "Hello")."""
    from websockets.asyncio.client import connect
    from websockets.exceptions import InvalidStatus

    ping_interval = None if idle_timeout else 20
    backoff = 2
    while True:
        try:
            async with connect(url, ssl=ctx, additional_headers=headers, proxy=None,
                               ping_interval=ping_interval, max_size=4 * 1024 * 1024) as ws:
                print(f"{now()} [{source}] verbunden", flush=True)
                backoff = 2
                while True:
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=idle_timeout)
                    except asyncio.TimeoutError:
                        print(f"{now()} [{source}] {idle_timeout:.0f} s ohne Lebenszeichen, neu verbinden", flush=True)
                        break
                    try:
                        msg = json.loads(raw)
                    except (TypeError, ValueError):
                        msg = None
                    if not isinstance(msg, dict):
                        # z. B. das "Hello" der Access-API als Lebenszeichen
                        if verbose:
                            print(f"{now()} [{source}] Text: {clean(str(raw).strip())[:200]}", flush=True)
                        log_line(fh, source, {"_text": clean(raw)[:2000]})
                        continue
                    log_line(fh, source, msg)
                    line = describe(msg)
                    if line:
                        print(f"{now()} [{source}] {line}", flush=True)
                    elif verbose:
                        kind = msg.get("event") or (msg.get("item") or {}).get("type") or msg.get("type")
                        print(f"{now()} [{source}] {clean(kind)}", flush=True)
        except InvalidStatus as err:
            status = err.response.status_code
            print(f"{now()} [{source}] Verbindung abgelehnt: HTTP {status}", flush=True)
            if status in (401, 403):
                print(f"{now()} [{source}] Zugang oder Berechtigung fehlt, kein neuer Versuch.", flush=True)
                return
        except (OSError, asyncio.TimeoutError) as err:
            print(f"{now()} [{source}] Verbindungsfehler: {clean(type(err).__name__)}: {clean(err)}", flush=True)
        except Exception as err:  # noqa: BLE001 – Testwerkzeug, soll weiterlaufen
            print(f"{now()} [{source}] getrennt: {clean(type(err).__name__)}: {clean(err)}", flush=True)
        await asyncio.sleep(backoff)
        backoff = min(backoff * 2, 60)


async def run_listen(args):
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, f"listen-{datetime.now():%Y%m%d-%H%M%S}.jsonl")
    tasks = []
    with open(path, "a", encoding="utf-8") as fh:
        if args.source == "both" and not os.environ.get("SIED_ACCESS_TOKEN", "").strip():
            print(f"{now()} Access übersprungen, SIED_ACCESS_TOKEN fehlt", flush=True)
        elif args.source in ("both", "access"):
            acc = Access()
            tasks.append(listen_ws("Access", acc.ws_url, acc.ctx, acc.headers, describe_access, fh, args.verbose,
                                   idle_timeout=15))
        if args.source == "both" and not os.environ.get("SIED_PROTECT_KEY", "").strip():
            print(f"{now()} Protect übersprungen, SIED_PROTECT_KEY fehlt", flush=True)
        elif args.source in ("both", "protect"):
            pro = Protect()
            tasks.append(listen_ws("Protect", pro.ws_url, pro.ctx, pro.headers, describe_protect, fh, args.verbose))
        print(f"{now()} Lausche {args.minutes} min. Protokoll: {path}", flush=True)
        try:
            await asyncio.wait_for(asyncio.gather(*tasks), timeout=args.minutes * 60)
        except asyncio.TimeoutError:
            print(f"{now()} Zeit abgelaufen, beendet.", flush=True)


def cmd_listen(args):
    asyncio.run(run_listen(args))


# ---------------------------------------------------------------- stream

def cmd_stream(args):
    """RTSPS-Stream einer Protect-Kamera prüfen: Codecs, Auflösung, Ton. Optional anzeigen."""
    camera = check_id(args.camera, "Kamera-ID")
    pro = Protect()
    status, payload = pro.call("GET", f"/cameras/{camera}/rtsps-stream")
    if status != 200:
        report_error("Protect rtsps-stream", status, payload)
        return
    urls = {k: v for k, v in (payload or {}).items() if isinstance(v, str) and v.startswith("rtsps://")}
    print(f"Aktive RTSPS-Streams: {', '.join(sorted(urls)) or 'keine'}", flush=True)

    if args.quality not in urls:
        if not args.create:
            print(f"Kein Stream in Qualität {args.quality!r}. Mit --create anlegen, "
                  f"das ändert die Kamera-Konfiguration (RTSPS-Freigabe).")
            return
        status, payload = pro.call("POST", f"/cameras/{camera}/rtsps-stream", {"qualities": [args.quality]})
        if status != 200:
            report_error("Protect rtsps-stream anlegen", status, payload)
            return
        urls = {k: v for k, v in (payload or {}).items() if isinstance(v, str) and v.startswith("rtsps://")}
        print(f"Angelegt: {', '.join(sorted(urls))}", flush=True)

    url = urls[args.quality]
    parts = urlsplit(url)
    if parts.hostname != pro.host:
        print(f"Hinweis: Stream liegt auf {clean(parts.hostname)}, nicht auf SIED_HOST.", flush=True)
    der = check_pin(parts.hostname, parts.port or RTSPS_PORT, "SIED_PIN_RTSPS")
    print(f"Stream {args.quality}: {mask_url(url)} (Zertifikat entspricht dem Pin)", flush=True)

    # ffmpeg (ab 9) prüft TLS selbst. Es bekommt das gepinnte Zertifikat als einzigen Vertrauensanker
    # und einen Namen, der im Zertifikat steht, weil die Konsole per IP angesprochen wird.
    os.makedirs(OUT_DIR, exist_ok=True)
    ca_file = os.path.join(OUT_DIR, "rtsps-pin.pem")
    with open(ca_file, "w", encoding="ascii") as fh:
        fh.write(ssl.DER_cert_to_PEM_cert(der))
    tls_opts = ["-tls_verify", "1", "-ca_file", ca_file, "-verifyhost", cert_dns_name(der)]
    probe_url = urlunsplit((parts.scheme, parts.netloc, parts.path, "", ""))
    cmd = ["ffprobe", "-v", "error", *tls_opts, "-rtsp_transport", "tcp", "-show_entries",
           "stream=index,codec_type,codec_name,profile,width,height,avg_frame_rate,sample_rate,channels",
           "-of", "json", probe_url]
    started = time.time()
    try:
        res = subprocess.run(cmd, capture_output=True, timeout=30)
    except subprocess.TimeoutExpired:
        die("ffprobe hat nach 30 s nicht geantwortet.")
    print(f"ffprobe: {time.time() - started:.1f} s bis zur Antwort", flush=True)
    if res.returncode != 0:
        print(clean(res.stderr.decode(errors="replace").replace(parts.path, "/***"))[:1000], flush=True)
        return
    for stream in json.loads(res.stdout).get("streams", []):
        print("  " + clean(json.dumps(stream, ensure_ascii=False)), flush=True)

    if args.play:
        subprocess.Popen(["ffplay", "-hide_banner", "-loglevel", "error", *tls_opts, "-fflags", "nobuffer",
                          "-flags", "low_delay", "-framedrop", "-rtsp_transport", "tcp",
                          "-window_title", "SI EntryDesk Test", probe_url],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        print("ffplay gestartet. Verzögerung: Uhr im Bild (OSD) mit der Mac-Uhr vergleichen.", flush=True)

    if args.delete:
        status, payload = pro.call("DELETE", f"/cameras/{camera}/rtsps-stream?qualities={quote(args.quality)}")
        print(f"Stream {args.quality} entfernt: HTTP {status}", flush=True)
        if status != 200 and payload:
            show(payload)


# ---------------------------------------------------------------- talkback

def cmd_talkback(args):
    """Talkback-Sitzung anlegen und Audioformat zeigen. Mit --tone einen kurzen Ton senden."""
    camera = check_id(args.camera, "Kamera-ID")
    pro = Protect()
    status, payload = pro.call("POST", f"/cameras/{camera}/talkback-session")
    print(f"Talkback-Sitzung: HTTP {status}", flush=True)
    if payload:
        show(payload)
    if status != 200 or not args.tone:
        return
    url, codec, rate = payload.get("url", ""), payload.get("codec"), payload.get("samplingRate")
    if not url.startswith("rtp://") or codec != "opus" or not isinstance(rate, int):
        die("Unerwartetes Format, Ton wird nicht gesendet.")
    print(f"Sende 1,5 s Ton (660 Hz, leise) als Opus {rate} Hz an {clean(url)}", flush=True)
    res = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-re",
                          "-f", "lavfi", "-i", "sine=frequency=660:duration=1.5",
                          "-af", "volume=0.3", "-c:a", "libopus", "-ar", str(rate), "-ac", "1",
                          "-b:a", "32k", "-f", "rtp", url], capture_output=True, timeout=30)
    print(f"ffmpeg beendet mit {res.returncode}", flush=True)
    if res.stderr:
        print(clean(res.stderr.decode(errors="replace"))[:1000], flush=True)


# ---------------------------------------------------------------- unlock

def cmd_unlock(args):
    """Öffnet eine echte Tür. Nur mit Freigabe und jemandem vor Ort."""
    if not args.ja_tuer_oeffnen:
        die("Öffnet eine echte Tür. Nur mit Freigabe und jemandem vor Ort, dann mit --ja-tuer-oeffnen.")
    door = check_id(args.door, "Tür-ID")
    acc = Access("SIED_ACCESS_UNLOCK_TOKEN" if os.environ.get("SIED_ACCESS_UNLOCK_TOKEN", "").strip()
                 else "SIED_ACCESS_TOKEN")
    body = {"actor_id": "sientrydesk-test", "actor_name": "SI EntryDesk Test"}
    status, payload = acc.call("PUT", f"/doors/{door}/unlock", body)
    print(f"{now()} Öffnen: HTTP {status}", flush=True)
    if payload:
        show(payload)
    if status in (401, 403):
        print("Hinweis: Öffnen braucht einen Token mit edit:space.", flush=True)


# ---------------------------------------------------------------- main

def main():
    parser = argparse.ArgumentParser(description="SI EntryDesk Machbarkeitstest")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("cert", help="Zertifikats-Fingerprints der Konsole anzeigen").set_defaults(func=cmd_cert)
    sub.add_parser("scopes", help="Leserechte der Access-Tokens prüfen (nur Status)").set_defaults(func=cmd_scopes)
    sub.add_parser("inventory", help="Geräte, Türen und Kameras auflisten (nur lesend)").set_defaults(func=cmd_inventory)

    p = sub.add_parser("listen", help="Klingel-Ereignisse von Access und Protect mitschreiben")
    p.add_argument("--source", choices=("both", "access", "protect"), default="both")
    p.add_argument("--minutes", type=float, default=30)
    p.add_argument("--verbose", action="store_true", help="auch andere Ereignisse anzeigen")
    p.set_defaults(func=cmd_listen)

    p = sub.add_parser("stream", help="RTSPS-Stream einer Kamera prüfen")
    p.add_argument("camera")
    p.add_argument("--quality", choices=("high", "medium", "low"), default="medium")
    p.add_argument("--create", action="store_true", help="Stream anlegen, falls keiner aktiv ist")
    p.add_argument("--play", action="store_true", help="mit ffplay anzeigen")
    p.add_argument("--delete", action="store_true", help="Stream danach wieder entfernen")
    p.set_defaults(func=cmd_stream)

    p = sub.add_parser("talkback", help="Talkback-Sitzung anlegen")
    p.add_argument("camera")
    p.add_argument("--tone", action="store_true", help="kurzen Ton an die Türstation senden")
    p.set_defaults(func=cmd_talkback)

    p = sub.add_parser("unlock", help="Tür öffnen (nur mit Freigabe)")
    p.add_argument("door")
    p.add_argument("--ja-tuer-oeffnen", action="store_true")
    p.set_defaults(func=cmd_unlock)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
