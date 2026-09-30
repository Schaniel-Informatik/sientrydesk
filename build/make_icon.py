#!/usr/bin/env python3
"""Erzeugt das Programm-Icon (Klingel auf grünem Kreis) als .ico mit PNG-Einträgen von 16 bis 256 px.

Ohne Fremdbibliotheken: Die Formen werden mit 4-fachem Überabtasten gerastert. Dieselbe Geometrie wie das
Tray-Symbol in TrayIcon.cs, dort in 16er-Einheiten.
Aufruf: python3 build/make_icon.py
"""
import os
import struct
import zlib

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
GREEN = (40, 200, 90)
WHITE = (255, 255, 255)
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "SIEntryDesk.App", "Assets", "SIEntryDesk.ico")


def in_circle(x, y):
    return (x - 8) ** 2 + (y - 8) ** 2 <= 7.8 ** 2


def in_bell(x, y):
    # Glockenkörper: Kuppel (obere Hälfte einer Ellipse), darunter nach aussen schwingende Flanken,
    # ein Rand mit runden Enden und der Klöppel.
    cx, cy, rx, ry = 8.0, 6.8, 4.2, 4.4
    dome = y <= cy and ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1
    t = (y - cy) / (10.6 - cy)
    flank = cy <= y <= 10.6 and abs(x - cx) <= rx + 1.3 * t ** 2
    rim_y, rim_r = 11.1, 0.75
    rim = (3.2 <= x <= 12.8 and abs(y - rim_y) <= rim_r) or \
          (x - 3.2) ** 2 + (y - rim_y) ** 2 <= rim_r ** 2 or (x - 12.8) ** 2 + (y - rim_y) ** 2 <= rim_r ** 2
    clapper = (x - 8) ** 2 + (y - 12.9) ** 2 <= 1.35 ** 2
    return dome or flank or rim or clapper


def render(size):
    ss = 4
    rows = []
    for py in range(size):
        row = bytearray([0])  # PNG-Filter: keiner
        for px in range(size):
            circle = bell = 0
            for sy in range(ss):
                for sx in range(ss):
                    x = (px + (sx + 0.5) / ss) * 16 / size
                    y = (py + (sy + 0.5) / ss) * 16 / size
                    if in_circle(x, y):
                        circle += 1
                        if in_bell(x, y):
                            bell += 1
            n = ss * ss
            alpha = circle / n
            if circle == 0:
                row += bytes((0, 0, 0, 0))
                continue
            w = bell / circle
            r, g, b = (round(GREEN[i] * (1 - w) + WHITE[i] * w) for i in range(3))
            row += bytes((r, g, b, round(alpha * 255)))
        rows.append(bytes(row))
    return png(size, b"".join(rows))


def png(size, raw):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    images = [(s, render(s)) for s in SIZES]
    offset = 6 + 16 * len(images)
    ico = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    for size, data in images:
        dim = 0 if size == 256 else size
        ico += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        ico += data
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as fh:
        fh.write(ico)
    print(f"{os.path.normpath(OUT)}: {len(images)} Grössen, {len(ico)} Bytes")


if __name__ == "__main__":
    main()
