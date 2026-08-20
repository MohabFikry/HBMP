#!/usr/bin/env python3
"""The docs' service inventory must equal the one on disk (2026-08-09 audit §3).

THE FAILURE THIS EXISTS FOR. `services/` holds 22 services. `docs/HANDOFF.md` said 21, CLAUDE.md's repository
layout listed 14, and `HBMP-Design/16-service-architecture.md` — the architecture document, the one somebody
reads to find out what this platform is made of — had a service table with 17 rows, omitting admin, case,
finance, interop and masterdata entirely.

None of those numbers was ever wrong when it was written. They were each correct at some phase and then a
service was added, and nothing connected the three of them to the directory or to each other. That is the
whole shape of doc drift: not carelessness, but three independent copies of one fact and no way to notice
when they stop agreeing.

WHY A GATE AND NOT JUST A CORRECTION. Fixing the three numbers today resets the clock and nothing more. The
next service lands in `services/` and all three go stale again on the same afternoon, silently, because a
document cannot fail. This makes it fail.

WHAT IT CHECKS
  1. every service directory appears in doc 16's service table
  2. the table names nothing that does not exist on disk (a deleted service left in the docs is the same
     defect pointing the other way, and is worse — it sends a reader looking for code that is not there)
  3. CLAUDE.md's repository layout lists every service
  4. every stated COUNT in the docs equals the real one

    check-service-inventory.py [--selftest]
"""
from __future__ import annotations

import argparse
import os
import re
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

DOC16 = "HBMP-Design/16-service-architecture.md"
CLAUDE_MD = "CLAUDE.md"
HANDOFF = "docs/HANDOFF.md"

# Directories under services/ that are not a deployable service. Empty today — kept so that adding one is a
# named, reviewable decision rather than a silent hole in the sweep.
NOT_A_SERVICE: set[str] = set()


def services_on_disk() -> set[str]:
    root = os.path.join(REPO, "services")
    return {
        name for name in os.listdir(root)
        if os.path.isdir(os.path.join(root, name)) and not name.startswith(".") and name not in NOT_A_SERVICE
    }


def read(rel: str) -> str:
    with open(os.path.join(REPO, rel), encoding="utf-8") as fh:
        return fh.read()


def doc16_table_services(src: str) -> set[str]:
    """Service names from the bolded first column of doc 16's catalog table.

    `**identity/auth**` is the identity service under an older name for the row's remit; it is mapped rather
    than renamed, because the row describes auth as well as identity and the heading is doing real work.
    """
    names = set()
    for cell in re.findall(r"^\|\s*\*\*([^*]+)\*\*", src, re.M):
        cell = cell.strip()
        if cell.startswith("identity"):
            names.add("identity")
            continue
        m = re.match(r"^([a-z][a-z0-9-]*)-service$", cell)
        if m:
            names.add(m.group(1))
    return names


def claude_layout_services(src: str) -> set[str]:
    """Service names from the `/services/` block of CLAUDE.md's repository layout."""
    # `[^\n]*` after the heading: the line may carry a trailing comment, and a document should not
    # have to keep its formatting still for a parser.
    block = re.search(r"^/services/[^\n]*\n((?:  .+\n)+)", src, re.M)
    if not block:
        return set()
    return set(re.findall(r"([a-z][a-z0-9-]*)/", block.group(1)))


def stated_counts(src: str, rel: str) -> list[tuple[str, int]]:
    """Every "<n> … services" claim in a document, with the phrase it appeared in.

    Line by line, taking the nearest number BEFORE the word — and skipping a number that is part of a
    technology version. The real sentence is "21 .NET 8 services", where a regex reaching backwards for the
    closest digits finds the 8 in `.NET 8` and reports the platform as having eight services. Getting that
    wrong in a gate about wrong numbers would be its own small joke.
    """
    out = []
    for line in src.splitlines():
        numbers = [(m.start(), int(m.group(1))) for m in re.finditer(r"\b(\d{1,3})\b", line)
                   if not re.search(r"(?:\.NET|NET|v|C#|version)\s*$", line[:m.start()], re.I)]
        # `(?!/)` — "services/audit:Domain" is a PATH, not a claim about how many there are. Without it the
        # gate reads the number in a coverage note three cells to the left as an inventory count.
        for word in re.finditer(r"\bservices\b(?!/)", line):
            before = [(pos, n) for pos, n in numbers if pos < word.start() and word.start() - pos <= 45]
            if not before:
                continue
            pos, stated = before[-1]
            out.append((f"{rel}: “{line[pos:word.end()].strip()}”", stated))
    return out


def check(disk: set[str], doc16: str, claude: str, handoff: str) -> list[str]:
    problems = []

    table = doc16_table_services(doc16)
    for missing in sorted(disk - table):
        problems.append(
            f"{DOC16} has no table row for `{missing}-service`, which exists in services/{missing}. The "
            "architecture document is where somebody looks to find out what this platform is made of.")
    for phantom in sorted(table - disk):
        problems.append(
            f"{DOC16} describes `{phantom}-service`, which is not in services/. Either it was deleted and the "
            "row outlived it, or the name changed — both send a reader looking for code that is not there.")

    layout = claude_layout_services(claude)
    if not layout:
        problems.append(f"{CLAUDE_MD}: could not find the `/services/` block of the repository layout — the "
                        "sweep below would pass on nothing. Has the layout been reformatted?")
    for missing in sorted(disk - layout):
        problems.append(f"{CLAUDE_MD}'s repository layout omits `{missing}/`.")
    for phantom in sorted(layout - disk):
        problems.append(f"{CLAUDE_MD}'s repository layout lists `{phantom}/`, which does not exist.")

    for where, stated in stated_counts(handoff, HANDOFF) + stated_counts(claude, CLAUDE_MD):
        # Only counts in the plausible range are read as inventory claims; "26 testing strategy services"
        # and the like are not, and neither is a version number that happens to sit near the word.
        if 5 <= stated <= 99 and stated != len(disk):
            problems.append(
                f"{where} — there are {len(disk)}. If this number is about something other than the service "
                "inventory, reword it so it cannot be read as a count of services.")

    return problems


def selftest() -> int:
    ok = True
    disk = {"patient", "policy", "audit"}
    good16 = "| **patient-service** | x |\n| **policy-service** | y |\n| **audit-service** | z |\n"
    good_claude = "/services/\n  patient/  policy/  audit/\n/apps/\n"
    good_handoff = "3 .NET 8 services, a SPA.\n"

    cases = [
        ("a matching set passes", disk, good16, good_claude, good_handoff, 0),
        ("a service missing from doc 16 fails",
         disk, "| **patient-service** | x |\n| **policy-service** | y |\n", good_claude, good_handoff, 1),
        ("a service missing from the CLAUDE.md layout fails",
         disk, good16, "/services/\n  patient/  policy/\n/apps/\n", good_handoff, 1),
        ("a doc describing a service that does not exist fails",
         disk, good16 + "| **ghost-service** | q |\n", good_claude, good_handoff, 1),
        ("a stale COUNT fails, and is the case all three documents got wrong",
         disk, good16, good_claude, "21 .NET 8 services, a SPA.\n", 1),
        # The way a sweep like this goes quiet: the layout block is reformatted, the regex stops matching,
        # and "no services found" reads as "nothing missing".
        ("an unparseable layout fails loudly rather than passing on an empty set",
         disk, good16, "the layout moved elsewhere\n", good_handoff, 1 + len(disk)),
    ]
    for name, d, a, b, c, expected in cases:
        got = len(check(d, a, b, c))
        if got != expected:
            print(f"FAIL: {name} — expected {expected} problem(s), got {got}: {check(d, a, b, c)}")
            ok = False

    print("selftest: PASS — missing, phantom, stale-count and unparseable cases all behave"
          if ok else "selftest: FAIL")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        return selftest()

    disk = services_on_disk()
    problems = check(disk, read(DOC16), read(CLAUDE_MD), read(HANDOFF))
    if problems:
        print("::error::the documented service inventory does not match services/:")
        for p in problems:
            print(f"  - {p}")
        return 1
    print(f"service-inventory: OK — all {len(disk)} services are in doc 16's catalog and CLAUDE.md's layout, "
          "and every stated count agrees.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
