#!/usr/bin/env python3
"""Versioned, reproducible inventory of every Git-tracked LaunchFlow file.

Counts physical lines (same as wc -l except for a final unterminated line).
Separately estimates nonempty non-comment lines for source files. This is not a
complexity score and does not count Markdown prose as lines of code.
"""
from __future__ import annotations

import csv
from collections import Counter, defaultdict
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "dist"
VERSION = "0.7.38"
SOURCE_EXTS = {".cs", ".xaml", ".py", ".ps1", ".cmd", ".sh", ".iss", ".csproj", ".props", ".yml", ".yaml"}
COMMENT_PREFIXES = ("//", "#", "<!--", "/*", "*", "--")
TRACKED = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).split(b"\0")
items = []
for raw in TRACKED:
    if not raw:
        continue
    relative = raw.decode("utf-8")
    path = ROOT / relative
    if not path.is_file():
        continue
    data = path.read_bytes()
    if b"\0" in data or path.suffix.lower() in {".png", ".ico", ".jpg", ".jpeg", ".zip", ".exe", ".dll"}:
        items.append((relative, "binary", "", "", len(data)))
        continue
    try:
        decoded = data.decode("utf-8")
    except UnicodeDecodeError:
        items.append((relative, "binary", "", "", len(data)))
        continue
    lines = decoded.splitlines()
    is_source = path.suffix.lower() in SOURCE_EXTS
    code = sum(bool(line.strip()) and not line.lstrip().startswith(COMMENT_PREFIXES) for line in lines) if is_source else ""
    items.append((relative, "source" if is_source else "document", len(lines), code, len(data)))

items.sort(key=lambda entry: entry[0].casefold())
OUTPUT.mkdir(parents=True, exist_ok=True)
csv_path = OUTPUT / f"LaunchFlow-v{VERSION}-file-lines.csv"
with csv_path.open("w", encoding="utf-8", newline="") as fp:
    writer = csv.writer(fp)
    writer.writerow(["file", "type", "physical_lines", "estimated_source_lines", "bytes"])
    writer.writerows(items)

by_type = Counter(entry[1] for entry in items)
total_lines = sum(entry[2] for entry in items if isinstance(entry[2], int))
source_lines = sum(entry[2] for entry in items if entry[1] == "source")
source_code_lines = sum(entry[3] for entry in items if entry[1] == "source")
summary = [
    f"# LaunchFlow v{VERSION} — complete file and line inventory",
    "",
    "Generated from exactly the Git-tracked files of the build commit.",
    "A physical line is one line of text including blanks and comments; binary files have N/A.",
    "The source-line estimate excludes blank lines and lines beginning with a recognized comment prefix;",
    "it is a lightweight estimate, not a syntax-aware metric.",
    "",
    f"- Git-tracked files: **{len(items)}**",
    f"- Source files: **{by_type['source']}**",
    f"- Documentation and other text files: **{by_type['document']}**",
    f"- Binary assets: **{by_type['binary']}**",
    f"- Physical text lines (all text files): **{total_lines:,}**",
    f"- Physical source lines (source files only): **{source_lines:,}**",
    f"- Estimated nonempty/non-comment source lines: **{source_code_lines:,}**",
    "",
    "| File | Type | Physical lines | Estimated source lines |",
    "|---|---|---:|---:|",
]
for filename, type_, lines, code, _ in items:
    summary.append(f"| `{filename}` | {type_} | {lines if isinstance(lines, int) else 'N/A'} | {code if isinstance(code, int) else 'N/A'} |")
md_path = OUTPUT / f"LaunchFlow-v{VERSION}-file-lines.md"
md_path.write_text("\n".join(summary) + "\n", encoding="utf-8")
print(f"INVENTORY total={len(items)} source={by_type['source']} document={by_type['document']} binary={by_type['binary']} physical={total_lines} source_physical={source_lines} estimated_code={source_code_lines}")
print(f"Wrote {csv_path} and {md_path}")
