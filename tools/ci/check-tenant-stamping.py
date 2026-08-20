#!/usr/bin/env python3
"""Tenant-stamping census, driven off information_schema.

THE SIBLING OF check-tenant-isolation.py, AND A DIFFERENT QUESTION.

The fuzzer proves that a tenant cannot see another tenant's rows. This one proves that every row BELONGS to
a tenant in the first place. They are not the same property and the second is the one that fails quietly: a
row written with `tenant_id = ''` belongs to nobody, so RLS hides it from every real tenant and the isolation
fuzzer — which asks whether the wrong tenant can see it — reports the table as perfectly isolated. The row is
simply gone, and the person it was about disappears from the board.

WHY THIS IS A CONTROL AND NOT A TEST. The upstream cause is structural: 64 entities declare
`public string TenantId { get; set; } = "";`, so any write path that forgets to set it stores an unscoped row
and nothing complains. That defect has been found by hand three times in this repository — one queue ticket
in emr (24.x), sixty prescriptions in pharmacy (32.x), and the seven-table sweep this gate replaces — each
time by somebody who happened to look. Census beats memory.

WHY IT IS NOT A `CHECK (tenant_id <> '')` CONSTRAINT. Because on exactly one table the empty string MEANS
something, and a constraint cannot tell the two apart — see SANCTIONED below.

Usage:
  PGHOST=... PGPORT=... PGDATABASE=hbmp OWNER_USER=hbmp OWNER_PASSWORD=... \\
  python3 tools/ci/check-tenant-stamping.py [--selftest]

  tools/ci/with-test-db.sh python3 tools/ci/check-tenant-stamping.py
"""
from __future__ import annotations
import os, subprocess, sys, uuid

# ------------------------------------------------------------------------------------------------------
# THE ONE PLACE '' IS AN ANSWER RATHER THAN AN OMISSION.
#
# identity.role_scope's empty tenant is the PLATFORM DEFAULT GRANT SET — `RoleScope.PlatformDefault`, a named
# constant in services/identity/Domain/Scope.cs, and the fallback bucket `RoleScopeResolver` reads when a
# tenant has not been provisioned its own copy. Migration 0011's banner states it outright: "THE DEFAULT
# BUCKET. tenant_id = '' is the platform default grant set." Writing `CHECK (tenant_id <> '')` on this table
# would not clean anything up; it would delete the fallback and leave every unprovisioned tenant's users with
# no scopes at all.
#
# So the exemption is NAMED, with the constant it corresponds to, rather than the table being skipped
# silently. An entry here is a claim that somebody decided; the reason is the review surface.
# ------------------------------------------------------------------------------------------------------
SANCTIONED = {
    "identity.role_scope":
        "RoleScope.PlatformDefault — the platform default grant bucket every unprovisioned tenant falls "
        "back to (identity migration 0011, design 40 §2). Not an unstamped row: a sentinel the resolver reads.",
}

SKIP_SCHEMAS = ("information_schema", "pg_catalog", "migration")


def _psql(user: str, password: str, sql: str) -> str:
    env = dict(os.environ)
    env["PGPASSWORD"] = password
    env["PGUSER"] = user
    out = subprocess.run(
        ["psql", "-v", "ON_ERROR_STOP=1", "-tAq", "-c", sql],
        capture_output=True, text=True, env=env,
    )
    if out.returncode != 0:
        raise RuntimeError(f"psql failed as {user}: {out.stderr.strip()}\n  sql: {sql[:200]}")
    return out.stdout.strip()


class Db:
    def __init__(self) -> None:
        self.owner_user = os.environ.get("OWNER_USER", "hbmp")
        self.owner_pw = os.environ["OWNER_PASSWORD"]

    def owner(self, sql: str) -> str:
        return _psql(self.owner_user, self.owner_pw, sql)


def census(db: Db) -> list[tuple[str, int]]:
    """Every table carrying tenant_id, with its count of unstamped rows.

    Counted in ONE round trip via `query_to_xml`, because a per-table query would be 168 connections and the
    check would be slow enough that somebody would eventually stop running it. Read as the OWNER: this
    deliberately reads past RLS, since a row hidden from every tenant is exactly what is being looked for.
    """
    skip = ", ".join(f"'{s}'" for s in SKIP_SCHEMAS)
    rows = db.owner(f"""
        SELECT c.table_schema || '.' || c.table_name || '|' ||
               (xpath('/row/c/text()',
                      query_to_xml(format('select count(*) as c from %I.%I where tenant_id = ''''',
                                          c.table_schema, c.table_name),
                                   false, true, '')))[1]::text
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.column_name = 'tenant_id' AND t.table_type = 'BASE TABLE'
          AND c.table_schema NOT IN ({skip})
        ORDER BY 1""")
    out = []
    for line in rows.splitlines():
        if not line.strip():
            continue
        name, _, count = line.rpartition("|")
        out.append((name, int(count)))
    return out


def selftest(db: Db) -> int:
    """Guard the guard: plant an unstamped row and confirm the census reports it.

    A checker that cannot fail is not a check — and this one is a single SQL query, which is precisely the
    kind that can be quietly correct about the wrong thing (an empty result reads identical to a clean one).
    """
    name = f"stamp_selftest_{uuid.uuid4().hex[:8]}"
    db.owner(f"""
        CREATE SCHEMA IF NOT EXISTS stampcheck;
        CREATE TABLE stampcheck."{name}" (id serial PRIMARY KEY, tenant_id text NOT NULL);
        INSERT INTO stampcheck."{name}" (tenant_id) VALUES ('t-real'), ('');
    """)
    try:
        found = dict(census(db)).get(f"stampcheck.{name}")
    finally:
        db.owner(f'DROP TABLE stampcheck."{name}"')

    if found != 1:
        print(f"::error::selftest FAILED — a planted unstamped row was not counted (got {found!r}, wanted 1)")
        return 1
    print("✓ selftest: an unstamped row IS counted")
    return 0


def main() -> int:
    try:
        db = Db()
    except KeyError as missing:
        print(f"::error::{missing} is required (the census reads as the OWNER, deliberately past RLS)")
        return 2

    if "--selftest" in sys.argv:
        return selftest(db)

    rows = census(db)
    offenders = [(t, n) for t, n in rows if n > 0 and t not in SANCTIONED]
    sanctioned_seen = [(t, n) for t, n in rows if n > 0 and t in SANCTIONED]

    print(f"Tenant-stamping census: {len(rows)} table(s) carry tenant_id; "
          f"{len(SANCTIONED)} sanctioned sentinel(s)")
    for t, n in sanctioned_seen:
        print(f"  · {t}: {n} row(s) at the sentinel — {SANCTIONED[t]}")

    # A sanctioned entry whose table has NO sentinel rows is reported too. Either the fallback was emptied —
    # which would strand every unprovisioned tenant — or the exemption has outlived its reason and should be
    # deleted. Both are worth a sentence; neither is worth failing a build over.
    for t in SANCTIONED:
        if t in dict(rows) and dict(rows)[t] == 0:
            print(f"  ! {t}: sanctioned, but holds NO sentinel rows — check whether the exemption still applies")

    if offenders:
        print("\n❌ UNSTAMPED ROWS — these belong to no tenant, so RLS hides them from every real one:")
        for t, n in offenders:
            print(f"   {t}: {n} row(s) with tenant_id = ''")
        print("\nFind the write path that omitted the stamp. The row is not visible to the tenant it was")
        print("meant for, so nobody will report it missing — that is why this is a gate and not a bug report.")
        return 1

    print("✓ every tenant-scoped row carries a tenant; '' appears only where it is a documented sentinel")
    return 0


if __name__ == "__main__":
    sys.exit(main())
