#!/usr/bin/env python3
"""Collects all texts of SI EntryDesk (calls of Tr.User and Tr.Admin in src/) into a table for proofreading.

    python3 tools/texts.py texts.xlsx      table with one sheet for users (de/en/pl) and one for admins (de/en)
    python3 tools/texts.py --check         only check that every call can be read (for the build)

Only the Python standard library. The arguments of Tr.User and Tr.Admin must be string literals (also interpolated,
verbatim or raw, joined with +). Parts in {…} are filled in by the program and stay as they are.
"""
import pathlib
import re
import sys
import zipfile
from xml.sax.saxutils import escape

ROOT = pathlib.Path(__file__).resolve().parent.parent
CALL = re.compile(r"\bTr\.(User|Admin)\s*\(")

# Where a text appears, by file. Everything else counts as "service" or "other".
PLACES = {
    "RingWindow": "ring window",
    "TrayIcon": "tray menu",
    "TrayStatus": "tray menu (status)",
    "App.xaml": "notification",
    "TalkAudio": "tray menu (talk with) / check",
    "TalkSender": "ring window (microphone)",
    "RingSettings": "tray menu (ring setting)",
    "CallEndReason": "ring window (end of call)",
    "EntryDeskOptions": "tray menu (expiry)",
    "CallCoordinator": "ring window / tray menu (from the service)",
    "TalkRelay": "ring window (talking, from the service)",
    "ServiceState": "tray menu (from the service)",
    "CheckWindow": "check window",
    "LocalChecks": "check window (this PC)",
    "Diagnostics": "check window (installation)",
    "CheckItem": "check window",
    "SetupWindow": "setup wizard",
    "SetupChecks": "setup wizard",
    "Program": "service command line",
}


class ParseError(Exception):
    pass


def skip_space(s, i):
    while i < len(s):
        if s[i].isspace():
            i += 1
        elif s.startswith("//", i):
            i = s.index("\n", i) if "\n" in s[i:] else len(s)
        else:
            break
    return i


def read_hole(s, i):
    """i points after '{'. Returns the end index after the matching '}', skipping nested strings."""
    depth = 1
    while i < len(s):
        c = s[i]
        if c in "\"'@$":
            _, j = read_literal(s, i, allow_char=True)
            if j is not None:
                i = j
                continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    raise ParseError("unclosed interpolation")


def read_literal(s, i, allow_char=False):
    """Reads one C# string literal at i. Returns (text, end) or (None, None) if there is none."""
    if allow_char and s[i] == "'":
        j = i + 1
        while s[j] != "'":
            j += 2 if s[j] == "\\" else 1
        return s[i + 1:j], j + 1
    m = re.match(r'(\$*)(@?)(\$*)("+)', s[i:])
    if not m or (s[i] == "@" and not m.group(4)):
        return None, None
    interpolated = bool(m.group(1) or m.group(3))
    verbatim = bool(m.group(2))
    quotes = m.group(4)
    j = i + m.end()
    out = []
    if len(quotes) >= 3:  # raw string literal
        dollars = len(m.group(1) + m.group(3))
        end = s.index(quotes, j)
        body = s[j:end]
        lines = body.split("\n")
        if len(lines) > 1:  # multi-line: drop first and last line, remove the indentation of the closing quotes
            indent = len(lines[-1]) - len(lines[-1].lstrip())
            body = "\n".join(line[indent:] for line in lines[1:-1])
        if interpolated and dollars > 1:
            body = body.replace("{" * dollars, "{").replace("}" * dollars, "}")
        return body, end + len(quotes)
    while True:
        c = s[j]
        if c == '"':
            if verbatim and s[j + 1:j + 2] == '"':
                out.append('"')
                j += 2
                continue
            return "".join(out), j + 1
        if c == "\\" and not verbatim:
            nxt = s[j + 1]
            out.append({"n": "\n", "t": "\t", '"': '"', "\\": "\\", "'": "'"}.get(nxt, "\\" + nxt))
            j += 2
            continue
        if interpolated and c == "{":
            if s[j + 1] == "{":
                out.append("{")
                j += 2
                continue
            end = read_hole(s, j + 1)
            out.append(s[j:end])
            j = end
            continue
        if interpolated and c == "}" and s[j + 1] == "}":
            out.append("}")
            j += 2
            continue
        out.append(c)
        j += 1


def read_argument(s, i):
    """A literal or several joined with +. Returns (text, end)."""
    parts = []
    while True:
        i = skip_space(s, i)
        text, end = read_literal(s, i)
        if text is None:
            raise ParseError("argument is not a string literal")
        parts.append(text)
        i = skip_space(s, end)
        if s[i] != "+":
            return "".join(parts), i
        i += 1


def calls(path):
    s = path.read_text(encoding="utf-8-sig")
    for m in CALL.finditer(s):
        line = s.count("\n", 0, m.start()) + 1
        if s.rfind("///", s.rfind("\n", 0, m.start()), m.start()) >= 0:
            continue  # in a documentation comment
        kind = m.group(1)
        i = m.end()
        args = []
        try:
            while True:
                text, i = read_argument(s, i)
                args.append(text)
                i = skip_space(s, i)
                if s[i] == ")":
                    break
                if s[i] != ",":
                    raise ParseError(f"unexpected {s[i]!r}")
                i += 1
        except (ParseError, IndexError, ValueError) as e:
            raise ParseError(f"{path.relative_to(ROOT)}:{line}: Tr.{kind}: {e}") from None
        expected = 3 if kind == "User" else 2
        if len(args) != expected:
            raise ParseError(f"{path.relative_to(ROOT)}:{line}: Tr.{kind} with {len(args)} instead of {expected} texts")
        yield kind, line, args


def collect():
    rows = {"User": [], "Admin": []}
    for path in sorted((ROOT / "src").rglob("*.cs")):
        if "/obj/" in path.as_posix() or "/bin/" in path.as_posix() or path.name == "Tr.cs":
            continue
        place = next((p for key, p in PLACES.items() if path.stem.startswith(key)), "service / other")
        for kind, line, args in calls(path):
            rows[kind].append([place, f"{path.relative_to(ROOT / 'src')}:{line}", *args])
    # Same text at several places only once, with all places.
    for kind, items in rows.items():
        merged = {}
        for place, where, *texts in items:
            key = tuple(texts)
            if key in merged:
                merged[key][1] += f", {where}"
            else:
                merged[key] = [place, where, *texts]
        rows[kind] = list(merged.values())
    return rows


def cell(ref, value, style=0):
    value = escape(value).replace("\n", "&#10;")
    return f'<c r="{ref}" t="inlineStr" s="{style}"><is><t xml:space="preserve">{value}</t></is></c>'


def sheet_xml(header, rows, widths):
    cols = "".join(f'<col min="{i + 1}" max="{i + 1}" width="{w}" customWidth="1"/>' for i, w in enumerate(widths))
    out = [f'<row r="1">' + "".join(cell(f"{chr(65 + c)}1", h, 1) for c, h in enumerate(header)) + "</row>"]
    for r, row in enumerate(rows, start=2):
        out.append(f'<row r="{r}">' + "".join(cell(f"{chr(65 + c)}{r}", v, 2) for c, v in enumerate(row)) + "</row>")
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            '<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/>'
            f'</sheetView></sheetViews><cols>{cols}</cols><sheetData>{"".join(out)}</sheetData></worksheet>')


def write_xlsx(target, sheets):
    names = [name for name, *_ in sheets]
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml",
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                   '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                   '<Default Extension="xml" ContentType="application/xml"/>'
                   '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
                   '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
                   + "".join(f'<Override PartName="/xl/worksheets/sheet{i + 1}.xml" '
                             'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
                             for i in range(len(sheets)))
                   + "</Types>")
        z.writestr("_rels/.rels",
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                   '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
                   "</Relationships>")
        z.writestr("xl/workbook.xml",
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
                   'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>'
                   + "".join(f'<sheet name="{escape(n)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>' for i, n in enumerate(names))
                   + "</sheets></workbook>")
        z.writestr("xl/_rels/workbook.xml.rels",
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                   + "".join(f'<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" '
                             f'Target="worksheets/sheet{i + 1}.xml"/>' for i in range(len(sheets)))
                   + f'<Relationship Id="rId{len(sheets) + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>'
                   "</Relationships>")
        z.writestr("xl/styles.xml",
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
                   '<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>'
                   '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>'
                   '<borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs>'
                   '<cellXfs count="3"><xf/><xf fontId="1" applyFont="1"/>'
                   '<xf applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf></cellXfs></styleSheet>')
        for i, (_, header, rows, widths) in enumerate(sheets):
            z.writestr(f"xl/worksheets/sheet{i + 1}.xml", sheet_xml(header, rows, widths))


def main():
    try:
        rows = collect()
    except ParseError as e:
        sys.exit(f"texts.py: {e}")
    if sys.argv[1:] == ["--check"]:
        print(f"{len(rows['User'])} texts for users, {len(rows['Admin'])} for admins")
        return
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    note = "Parts in {…} are filled in by the program, keep them."
    write_xlsx(sys.argv[1], [
        ("Users (de, en, pl)",
         ["Where", "Source", "German (original)", "English", "Polish (draft)", "Polish correction", "English correction", "Comment"],
         [[p, w, de, en, pl, "", "", ""] for p, w, de, en, pl in rows["User"]] + [["", "", note, "", "", "", "", ""]],
         [24, 18, 45, 45, 45, 45, 30, 30]),
        ("Admins (de, en)",
         ["Where", "Source", "German (original)", "English", "English correction", "Comment"],
         [[p, w, de, en, "", ""] for p, w, de, en in rows["Admin"]] + [["", "", note, "", "", ""]],
         [24, 18, 60, 60, 45, 30]),
    ])
    print(f"{sys.argv[1]}: {len(rows['User'])} texts for users, {len(rows['Admin'])} for admins")


if __name__ == "__main__":
    main()
