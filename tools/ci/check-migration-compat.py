#!/usr/bin/env python3
"""Expand/contract backward-compatibility gate for hand-authored SQL migrations (phase 12.2).

The rule (25-deployment-architecture.md §8): a migration that deploys alongside the currently-running
service version must be BACKWARD-COMPATIBLE. Destructive/narrowing changes (the "contract" phase)
must ship in a SEPARATE, later migration — only after the new version is fully rolled out. This gate
flags contract-phase operations so they cannot sneak into an expand-phase migration.

Contract-phase operations flagged:
  - DROP TABLE / DROP COLUMN / DROP SCHEMA / DROP SEQUENCE / DROP TYPE
  - ALTER COLUMN ... SET NOT NULL          (old writers may still insert NULL)
  - ALTER COLUMN ... TYPE                  (type change can break the running version)
  - ADD COLUMN ... NOT NULL (no DEFAULT)   (old writers' inserts fail)
  - RENAME TABLE / RENAME COLUMN           (old version references the old name)
  - DROP CONSTRAINT / DROP DEFAULT

Intentional contract migrations (deployed post-rollout) acknowledge each finding with a trailing
comment on the offending statement:
    ALTER TABLE t DROP COLUMN old_col;  -- migrate-compat: contract-ok (dropped after v2 rollout)

Modes:
  --all            scan every migration file in the repo (audit)
  --diff <base>    scan only files added/changed vs <base> (PR gate; default base: origin/master)
  --selftest       verify the detector on synthetic good/bad snippets and exit

Exit code 0 = clean, 1 = unacknowledged contract op found, 2 = usage error.
"""
from __future__ import annotations

import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
MIGRATION_GLOBS = ("services/*/Infrastructure/Migrations/*.sql", "tools/migration/Migrations/*.sql")
ACK = "migrate-compat: contract-ok"

# (rule id, compiled regex) — matched per-statement, case-insensitive.
RULES: list[tuple[str, re.Pattern[str]]] = [
    ("drop-table", re.compile(r"\bDROP\s+TABLE\b", re.I)),
    ("drop-column", re.compile(r"\bDROP\s+COLUMN\b", re.I)),
    ("drop-schema", re.compile(r"\bDROP\s+SCHEMA\b", re.I)),
    ("drop-sequence", re.compile(r"\bDROP\s+SEQUENCE\b", re.I)),
    ("drop-type", re.compile(r"\bDROP\s+TYPE\b", re.I)),
    ("drop-constraint", re.compile(r"\bDROP\s+CONSTRAINT\b", re.I)),
    ("drop-default", re.compile(r"\bALTER\s+COLUMN\b.*\bDROP\s+DEFAULT\b", re.I | re.S)),
    ("set-not-null", re.compile(r"\bALTER\s+COLUMN\b.*\bSET\s+NOT\s+NULL\b", re.I | re.S)),
    ("alter-type", re.compile(r"\bALTER\s+COLUMN\b.*\b(SET\s+DATA\s+)?TYPE\b", re.I | re.S)),
    ("rename-table", re.compile(r"\bALTER\s+TABLE\b.*\bRENAME\s+TO\b", re.I | re.S)),
    ("rename-column", re.compile(r"\bRENAME\s+COLUMN\b", re.I)),
    # ADD COLUMN ... NOT NULL without a DEFAULT: old writers' inserts would fail.
    ("add-notnull-no-default", re.compile(
        r"\bADD\s+COLUMN\b(?:(?!\bDEFAULT\b).)*?\bNOT\s+NULL\b(?!.*\bDEFAULT\b)", re.I | re.S)),
]


@dataclass
class Finding:
    file: str
    line: int
    rule: str
    text: str


def split_statements(sql: str) -> list[tuple[int, str]]:
    """Split into statements, tracking the 1-based start line of each. Comments are kept so the
    acknowledgement comment travels with its statement; dollar-quoted bodies aren't split."""
    statements, buf, start_line, line = [], [], 1, 1
    in_dollar = False
    i = 0
    while i < len(sql):
        ch = sql[i]
        if sql.startswith("$$", i):
            in_dollar = not in_dollar
            buf.append("$$"); i += 2
            continue
        if ch == ";" and not in_dollar:
            buf.append(";")
            # Pull the trailing same-line comment into THIS statement so an inline
            # "-- migrate-compat: contract-ok" acknowledgement travels with its statement.
            j = i + 1
            while j < len(sql) and sql[j] != "\n":
                buf.append(sql[j]); j += 1
            statements.append((start_line, "".join(buf)))
            buf, start_line = [], line
            i = j
            continue
        if ch == "\n":
            line += 1
        buf.append(ch)
        i += 1
    if "".join(buf).strip():
        statements.append((start_line, "".join(buf)))
    return statements


def strip_line_comments(stmt: str) -> str:
    return "\n".join(re.sub(r"--.*$", "", ln) for ln in stmt.splitlines())


def scan_file(path: Path) -> list[Finding]:
    findings: list[Finding] = []
    sql = path.read_text(encoding="utf-8")
    rel = str(path.relative_to(REPO))
    for start_line, stmt in split_statements(sql):
        acknowledged = ACK in stmt  # trailing comment anywhere in the statement acknowledges it
        code = strip_line_comments(stmt)
        for rule_id, pattern in RULES:
            if pattern.search(code) and not acknowledged:
                findings.append(Finding(rel, start_line, rule_id, first_nonblank(code)))
    return findings


def first_nonblank(stmt: str) -> str:
    for ln in stmt.splitlines():
        if ln.strip():
            return ln.strip()[:120]
    return stmt.strip()[:120]


def migration_files() -> list[Path]:
    files: list[Path] = []
    for g in MIGRATION_GLOBS:
        files.extend(sorted(REPO.glob(g)))
    return files


def changed_files(base: str) -> list[Path]:
    try:
        out = subprocess.check_output(
            ["git", "-C", str(REPO), "diff", "--name-only", "--diff-filter=AM", f"{base}...HEAD"],
            text=True, stderr=subprocess.DEVNULL)
    except subprocess.CalledProcessError:
        out = subprocess.check_output(
            ["git", "-C", str(REPO), "diff", "--name-only", "--diff-filter=AM", base],
            text=True)
    tracked = {p for g in MIGRATION_GLOBS for p in REPO.glob(g)}
    return [REPO / name for name in out.splitlines() if (REPO / name) in tracked]


SELFTEST_BAD = [
    ("drop-column", "ALTER TABLE patient DROP COLUMN legacy_mrn;"),
    ("set-not-null", "ALTER TABLE policy ALTER COLUMN tier SET NOT NULL;"),
    ("add-notnull-no-default", "ALTER TABLE claim ADD COLUMN status text NOT NULL;"),
    ("rename-column", "ALTER TABLE emr RENAME COLUMN note TO clinical_note;"),
    ("drop-table", "DROP TABLE old_staging;"),
]
SELFTEST_GOOD = [
    "ALTER TABLE claim ADD COLUMN status text;",                                  # nullable add
    "ALTER TABLE claim ADD COLUMN status text NOT NULL DEFAULT 'open';",          # NOT NULL w/ default
    "CREATE TABLE foo (id uuid PRIMARY KEY);",
    "CREATE INDEX ix_foo ON foo (id);",
    "ALTER TABLE t ADD CONSTRAINT c CHECK (x > 0) NOT VALID;",
    "ALTER TABLE patient DROP COLUMN legacy_mrn;  -- migrate-compat: contract-ok (post-v2)",
]


def selftest() -> int:
    ok = True
    for expected_rule, sql in SELFTEST_BAD:
        rules = {r for _, stmt in split_statements(sql)
                 for r, pat in RULES if pat.search(strip_line_comments(stmt)) and ACK not in stmt}
        if expected_rule not in rules:
            print(f"  SELFTEST FAIL: expected '{expected_rule}' for: {sql}  (got {rules})")
            ok = False
    for sql in SELFTEST_GOOD:
        rules = {r for _, stmt in split_statements(sql)
                 for r, pat in RULES if pat.search(strip_line_comments(stmt)) and ACK not in stmt}
        if rules:
            print(f"  SELFTEST FAIL: expected clean but flagged {rules} for: {sql}")
            ok = False
    print("selftest: PASS" if ok else "selftest: FAIL")
    return 0 if ok else 1


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()

    if "--diff" in argv:
        base = argv[argv.index("--diff") + 1] if argv.index("--diff") + 1 < len(argv) else "origin/master"
        files = changed_files(base)
        scope = f"changed vs {base}"
    elif "--all" in argv or len(argv) == 0:
        files = migration_files()
        scope = "all migrations"
    else:
        print(__doc__)
        return 2

    findings: list[Finding] = []
    for f in files:
        findings.extend(scan_file(f))

    print(f"migration-compat gate: scanned {len(files)} file(s) ({scope}).")
    if not findings:
        print("OK — no unacknowledged contract-phase operations.")
        return 0

    print(f"\nFOUND {len(findings)} contract-phase operation(s) in an expand-phase migration:\n")
    for x in findings:
        print(f"  {x.file}:{x.line}  [{x.rule}]  {x.text}")
    print("\nSplit these into a later contract migration (deployed after full rollout), or — if this")
    print(f"IS the post-rollout contract migration — acknowledge each with a trailing '-- {ACK} (reason)'.")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
