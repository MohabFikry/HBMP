#!/usr/bin/env python3
"""Phase 24 Gate 7 — the status document must not contradict itself.

docs/BUILD-STATUS.md is what anyone reads to answer "where is this project?". It had eleven sub-prompt
ids listed TWICE with opposite status glyphs — one row saying shipped, another saying not started, for
the same piece of work — and five different glyphs in use with no legend saying what any of them mean.
A reader cannot tell which row is stale, so they believe whichever one they read first.

Neither problem is visible when you read the file top to bottom, which is why it survived: the two rows
for 18.D3 are hundreds of lines apart.

Checks:
  1. no sub-prompt id appears with two different status glyphs
  2. every glyph used is defined in the legend
  3. the legend defines no glyph the table never uses (a legend that describes a vocabulary nobody
     speaks is how the next stale entry gets excused)

Usage: check-build-status.py [path]   |   --selftest
"""
from __future__ import annotations

import re
import sys
import tempfile
from pathlib import Path

DEFAULT = "docs/BUILD-STATUS.md"
LEGEND_RE = re.compile(r"^\s*[-*]?\s*(?P<glyph>\S+)\s*[=—–:]\s*(?P<meaning>.+)$")
# Sub-prompt ids are 18.B2, 19.6b, 20.3b, 10b.4 — the segment after a dot is not always numeric.
# An earlier `\.\d+` version could not match any of them, so every 18.x row collapsed to the id "18"
# and the checker compared statuses across unrelated work while appearing to find real conflicts.
ID_RE = re.compile(r"^(\d+[A-Za-z]?(?:\.[0-9A-Za-z]+)*)")


def parse(text: str) -> tuple[dict[str, set[str]], set[str], dict[str, str]]:
    """(id -> glyphs seen, glyphs used, legend)."""
    ids: dict[str, set[str]] = {}
    used: set[str] = set()
    legend: dict[str, str] = {}

    in_legend = False
    for raw in text.splitlines():
        line = raw.strip()

        if line.lower().startswith("## legend") or line.lower().startswith("### legend"):
            in_legend = True
            continue
        if in_legend:
            if line.startswith("#") or line.startswith("|"):
                in_legend = False
            elif (m := LEGEND_RE.match(line)):
                legend[m.group("glyph")] = m.group("meaning").strip()
                continue
            elif not line:
                continue

        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.split("|")]
        if len(cells) < 6 or set(cells[1]) <= set("- "):
            continue
        subject, status = cells[3], cells[4]
        if not status or status.lower() == "status":
            continue
        used.add(status)
        if (m := ID_RE.match(subject)):
            ids.setdefault(m.group(1), set()).add(status)

    return ids, used, legend


def report(ids: dict[str, set[str]], used: set[str], legend: dict[str, str]) -> int:
    problems: list[str] = []

    conflicts = {k: v for k, v in ids.items() if len(v) > 1}
    for key in sorted(conflicts):
        problems.append(f"{key} is listed with conflicting statuses: {' '.join(sorted(conflicts[key]))}")

    for glyph in sorted(used - set(legend)):
        problems.append(f"status glyph {glyph!r} is used but not defined in the legend")

    for glyph in sorted(set(legend) - used):
        problems.append(f"legend defines {glyph!r} but nothing uses it")

    print(f"build-status gate: {len(ids)} sub-prompt id(s), {len(used)} glyph(s), {len(legend)} legend entr(ies)")
    if problems:
        print()
        for p in problems:
            print(f"  {p}")
        print()
        print("::error::the status document contradicts itself. A reader cannot tell which row is stale,")
        print("so they believe whichever one they read first — and this is the file people read to decide")
        print("what is already built.")
        return 1
    print("build-status gate: OK — no contradictions, every glyph defined and used")
    return 0


GOOD = """## Legend
- ☑ = shipped
- ☐ = not started

| Phase | Area | Sub-prompt | Status | Notes |
|---|---|---|---|---|
| 1 | X | 1.1 does a thing | ☑ | fine |
| 2 | Y | 2.1 another | ☐ | fine |
"""

CONFLICT = GOOD + "| 1 | X | 1.1 does a thing | ☐ | stale duplicate |\n"
UNDEFINED = GOOD + "| 3 | Z | 3.1 third | ◪ | undefined glyph |\n"
UNUSED = GOOD.replace("- ☐ = not started", "- ☐ = not started\n- ◐ = in progress")


def selftest() -> int:
    failures: list[str] = []
    cases = [("clean", GOOD, 0), ("conflict", CONFLICT, 1), ("undefined glyph", UNDEFINED, 1),
             ("unused legend entry", UNUSED, 1)]
    for name, text, expected in cases:
        got = report(*parse(text))
        if got != expected:
            failures.append(f"{name}: expected exit {expected}, got {got}")

    # The conflict must be found even when the duplicate rows are far apart, which is the real shape.
    far = GOOD + "\n" + ("\nfiller line" * 400) + "\n| 1 | X | 1.1 does a thing | ☐ | stale |\n"
    if report(*parse(far)) != 1:
        failures.append("a conflict separated by hundreds of lines was missed")

    if failures:
        for f in failures:
            print(f"SELFTEST FAIL: {f}")
        return 1
    print("check-build-status selftest: OK")
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    path = Path(argv[1]) if len(argv) > 1 else Path(DEFAULT)
    if not path.exists():
        print(f"::error::{path} not found", file=sys.stderr)
        return 1
    return report(*parse(path.read_text(encoding="utf-8")))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
