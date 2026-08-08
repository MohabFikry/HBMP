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
  4. no row marked "not started" is described in the prose as delivered

Check 4 came from a defect checks 1-3 could not see. All six of phase 20's rows read "not started" while
the prose in the same file described 20.1, 20.2, 20.4 and 20.5 in detail as shipped — and profile-service,
its 86 tests, its OpenAPI spec, its ADR and its screen were all in the tree. No id appeared twice, so
check 1 was silent; every glyph was defined and used, so checks 2-3 were too. The contradiction was
between the TABLE and the PROSE, which nothing compared.

That shape is the more dangerous one. A duplicate row at least shows a reader that something is wrong; a
table that quietly under-reports has one answer, and it is the wrong one. Someone planning work from this
file would have rebuilt a service that already existed.

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
# The prose names a sub-prompt in bold: "**20.2** the four partial 360s are consolidated…", "**20.4
# completed:** …". At least one dot is REQUIRED so a bare phase number ("**Phase 20**") and a date
# ("**2026-07-29**") are not read as sub-prompt ids.
NARRATED_RE = re.compile(r"\*\*(\d+[A-Za-z]?(?:\.[0-9A-Za-z]+)+)")


def parse(text: str) -> tuple[dict[str, set[str]], set[str], dict[str, str], set[str]]:
    """(id -> glyphs seen, glyphs used, legend, ids the PROSE describes)."""
    ids: dict[str, set[str]] = {}
    used: set[str] = set()
    legend: dict[str, str] = {}
    narrated: set[str] = set()

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
            # Prose. A sub-prompt named in bold here is one this document is telling the reader about.
            narrated.update(NARRATED_RE.findall(line))
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

    return ids, used, legend, narrated


# The glyphs that mean "no work has happened". Kept as a set rather than a literal so the rule survives a
# vocabulary change: what matters is the CLAIM the row makes, not the character it makes it with.
NOT_STARTED = {"☐"}


def report(ids: dict[str, set[str]], used: set[str], legend: dict[str, str],
           narrated: set[str] | None = None) -> int:
    problems: list[str] = []

    conflicts = {k: v for k, v in ids.items() if len(v) > 1}
    for key in sorted(conflicts):
        problems.append(f"{key} is listed with conflicting statuses: {' '.join(sorted(conflicts[key]))}")

    for glyph in sorted(used - set(legend)):
        problems.append(f"status glyph {glyph!r} is used but not defined in the legend")

    for glyph in sorted(set(legend) - used):
        problems.append(f"legend defines {glyph!r} but nothing uses it")

    # 4 — the table says nothing was done; the prose describes what was done.
    for key in sorted({k for k, v in ids.items() if v <= NOT_STARTED} & (narrated or set())):
        problems.append(
            f"{key} is marked not-started in the table, and the prose of this same file describes it as "
            f"delivered. One of the two is wrong, and a reader planning work would believe the table")

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
# The phase-20 shape: no duplicate id, every glyph defined and used, and the prose contradicts the table.
NARRATED_BUT_NOT_STARTED = GOOD + "\n- **2.1** shipped on Tuesday, here is what it does.\n"
# The same prose against a row that CLAIMS to be shipped is the normal, correct case — a shipped row is
# exactly what the prose should be elaborating, and flagging it would make the rule unusable.
NARRATED_AND_SHIPPED = GOOD + "\n- **1.1** shipped on Tuesday, here is what it does.\n"
UNDEFINED = GOOD + "| 3 | Z | 3.1 third | ◪ | undefined glyph |\n"
UNUSED = GOOD.replace("- ☐ = not started", "- ☐ = not started\n- ◐ = in progress")


def selftest() -> int:
    failures: list[str] = []
    cases = [("clean", GOOD, 0), ("conflict", CONFLICT, 1), ("undefined glyph", UNDEFINED, 1),
             ("unused legend entry", UNUSED, 1),
             ("prose describes a not-started row", NARRATED_BUT_NOT_STARTED, 1),
             ("prose describes a shipped row", NARRATED_AND_SHIPPED, 0)]
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
