#!/usr/bin/env python3
"""Tenant-isolation fuzzer, driven off information_schema (Phase 18.F3).

THE CONTROL THAT WOULD HAVE CAUGHT X6 AND S2 AUTOMATICALLY.

Every other check in this repo asks a question someone thought to ask. This one asks the DATABASE what
tables exist and then proves isolation on each, so a table added next year in a service nobody is thinking
about is covered the day it lands. That is the difference between a test suite and a control: the six
hand-written RlsIsolationTests prove six schemas hold; this proves EVERY tenant-scoped table holds,
including the ones for which no test was written — which is exactly the set X6 lived in.

For each table carrying tenant_id, connecting as the NOBYPASSRLS runtime role:
  * a policy exists, RLS is ENABLED, and it is FORCEd (without FORCE the table owner bypasses it, so a
    maintenance session silently sees every tenant and an owner-run test passes for the wrong reason)
  * with the GUC UNSET  -> ZERO rows. This is the assertion a fail-OPEN policy cannot pass, whatever the
    data looks like, which is why it is the load-bearing one.
  * bound to tenant A   -> no row belonging to another tenant is visible

Runs on `psql` rather than a Python driver ON PURPOSE. tools/ci/apply-migrations.sh already talks to
Postgres this way, psql is present wherever migrations run, and adding a pip dependency would mean the
check is skipped in any environment that lacks it — which is how tools/ci/check-kong-route-coverage.py
spent two phases committed and never executed (Q1).

Usage:
  PGHOST=... PGPORT=... PGDATABASE=hbmp \\
  OWNER_USER=hbmp OWNER_PASSWORD=... APP_USER=hbmp_app APP_PASSWORD=... \\
  python3 tools/ci/check-tenant-isolation.py [--selftest]
"""
from __future__ import annotations
import os, subprocess, sys, uuid

TENANT_A = "11111111-1111-1111-1111-111111111111"

# Tables that carry a tenant column and are deliberately RLS-free. This register MUST agree with
# libs/architecture HousePatternTests.RlsFreeTables — two exemption lists that can drift apart is how an
# exception outlives its reason, so a test there asserts they match.
RLS_FREE = {
    "processed_event": "consumer dedupe ledger — read on the replay path before the tenant is resolved",
    "processed_request": "idempotency ledger — the key is looked up before a tenant exists",
    "outbox_message": "relay ledger, drained by a background publisher with no request principal",
    "tenant": "admin.tenant is the tenant REGISTRY — isolating it on its own PK would reduce a Super Admin's list to one row",
}

# Schemas whose isolation is deliberately NOT tenant-GUC based (18.B2): audit keys on ROLE MEMBERSHIP;
# identity is the authentication authority and must resolve a user BEFORE any tenant context exists.
SKIP_SCHEMAS = ("audit", "identity", "public", "information_schema", "pg_catalog", "migration")


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
        self.app_user = os.environ.get("APP_USER", "hbmp_app")
        self.app_pw = os.environ["APP_PASSWORD"]

    def owner(self, sql: str) -> str:
        return _psql(self.owner_user, self.owner_pw, sql)

    def app(self, sql: str) -> str:
        return _psql(self.app_user, self.app_pw, sql)

    def app_bound(self, tenant: str, sql: str) -> str:
        """Run `sql` in ONE session with app.tenant_id set — the GUC is per-session, so it and the query
        must share a connection or the binding is lost."""
        return _psql(self.app_user, self.app_pw,
                     f"SELECT set_config('app.tenant_id', '{tenant}', false); {sql}").splitlines()[-1]


def tenant_tables(db: Db) -> list[tuple[str, str]]:
    skip = ", ".join(f"'{s}'" for s in SKIP_SCHEMAS)
    rows = db.owner(f"""
        SELECT c.table_schema || '.' || c.table_name
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.column_name = 'tenant_id' AND t.table_type = 'BASE TABLE'
          AND c.table_schema NOT IN ({skip})
        ORDER BY 1""")
    return [tuple(r.split(".", 1)) for r in rows.splitlines() if r]


def check_table(db: Db, schema: str, table: str, failures: list[str]) -> None:
    if table in RLS_FREE:
        return

    state = db.owner(f"""
        SELECT c.relrowsecurity::int, c.relforcerowsecurity::int,
               (SELECT count(*) FROM pg_policies p WHERE p.schemaname = '{schema}' AND p.tablename = '{table}')
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = '{schema}' AND c.relname = '{table}'""")
    if not state:
        return
    enabled, forced, policies = (int(x) for x in state.split("|"))

    if policies == 0:
        failures.append(f"{schema}.{table}: carries tenant_id and has NO row-level policy — "
                        f"isolation rests entirely on whatever WHERE clause the application remembers")
        return
    if not enabled:
        failures.append(f"{schema}.{table}: has a policy but ROW LEVEL SECURITY is not ENABLED — it never runs")
        return
    if not forced:
        failures.append(f"{schema}.{table}: not FORCEd — the table OWNER bypasses the policy, so a migration "
                        f"or maintenance session silently sees every tenant")

    # THE load-bearing assertion. A fail-open policy (`OR current_setting(...) IS NULL`) returns rows here
    # regardless of what the table contains; a fail-closed one returns none whether the table is full or empty.
    try:
        unbound = int(db.app_bound("", f'SELECT count(*) FROM "{schema}"."{table}"'))
    except RuntimeError as e:
        failures.append(f"{schema}.{table}: could not be read as {db.app_user} — {e}")
        return
    if unbound != 0:
        # 24.5 — NAME THE RIGHT CAUSE. This probe binds app.tenant_id to the EMPTY STRING, so rows come
        # back for two very different reasons and the message used to assert only the first:
        #
        #   * a fail-open policy (`OR current_setting(...) IS NULL`), the shape 18.B2 removed; or
        #   * rows whose tenant_id IS the empty string, which match `tenant_id = ''` under a perfectly
        #     correct policy. Those rows belong to no tenant: invisible to every real tenant (so the
        #     application has effectively lost them) and visible to anything that binds an empty one.
        #
        # Reporting the second as "FAIL-OPEN policy" sends the reader to inspect pg_policy, find it
        # correct, and conclude the checker is broken — which is exactly what happened when this ran
        # against the dev database and found 1,191 such rows across seven tables. The usual source is a
        # C# entity declaring `public string TenantId { get; set; } = "";` and a write path that never
        # sets it.
        empty = int(db.owner(f"SELECT count(*) FROM \"{schema}\".\"{table}\" WHERE tenant_id = ''"))
        if empty:
            failures.append(
                f"{schema}.{table}: {empty} row(s) have an EMPTY tenant_id — they belong to no tenant, so "
                f"they are invisible to every real one and visible to any session binding an empty tenant. "
                f"The policy is fine; the data is not. Fix the write path, backfill, and add "
                f"CHECK (tenant_id <> '').")
        else:
            failures.append(f"{schema}.{table}: {unbound} row(s) visible with NO app.tenant_id bound — this is a "
                            f"FAIL-OPEN policy. A background or maintenance connection sees everything.")

    # Cross-tenant leakage on whatever real data is present.
    leaked = int(db.app_bound(
        TENANT_A, f"SELECT count(*) FROM \"{schema}\".\"{table}\" WHERE tenant_id <> '{TENANT_A}'"))
    if leaked:
        failures.append(f"{schema}.{table}: {leaked} row(s) belonging to another tenant are visible under tenant A")


def selftest(db: Db) -> int:
    """Guard the guard. Build a table with the exact fail-open shape 18.B2 removed from admin and interop,
    and confirm the fuzzer reports it. A checker that cannot fail is not a check."""
    name = f"fuzz_selftest_{uuid.uuid4().hex[:8]}"
    db.owner(f"""
        CREATE SCHEMA IF NOT EXISTS fuzzcheck;
        CREATE TABLE fuzzcheck."{name}" (id serial PRIMARY KEY, tenant_id text NOT NULL);
        GRANT USAGE ON SCHEMA fuzzcheck TO {db.app_user};
        GRANT SELECT, INSERT ON fuzzcheck."{name}" TO {db.app_user};
        ALTER TABLE fuzzcheck."{name}" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE fuzzcheck."{name}" FORCE ROW LEVEL SECURITY;
        CREATE POLICY p ON fuzzcheck."{name}"
            USING (tenant_id = current_setting('app.tenant_id', true)
                   OR current_setting('app.tenant_id', true) IS NULL
                   OR current_setting('app.tenant_id', true) = '');
        INSERT INTO fuzzcheck."{name}" (tenant_id) VALUES ('{TENANT_A}'), ('22222222-2222-2222-2222-222222222222');
    """)
    failures: list[str] = []
    try:
        check_table(db, "fuzzcheck", name, failures)
    finally:
        db.owner(f'DROP TABLE fuzzcheck."{name}"')

    if not any("FAIL-OPEN" in f for f in failures):
        print("::error::selftest FAILED — the fuzzer did not detect a deliberately fail-open policy")
        print(f"   (it reported: {failures or 'nothing'})")
        return 1
    print("✓ selftest: a fail-open policy IS detected")
    return 0


def main() -> int:
    try:
        db = Db()
    except KeyError as missing:
        print(f"::error::{missing} is required (OWNER_* seeds/inspects; APP_* is the NOBYPASSRLS role under test)")
        return 2

    if "--selftest" in sys.argv:
        return selftest(db)

    tables = tenant_tables(db)
    failures: list[str] = []
    for schema, table in tables:
        check_table(db, schema, table, failures)

    exempt = sum(1 for _, t in tables if t in RLS_FREE)
    print(f"Tenant-isolation fuzzer: {len(tables) - exempt} tenant-scoped table(s) proven, "
          f"{exempt} declared RLS-free")

    if failures:
        print("\n❌ TENANT ISOLATION FAILURES:")
        for f in failures:
            print(f"   {f}")
        return 1
    print("✓ every tenant-scoped table isolates: an unbound session sees nothing, a bound one sees only its own")
    return 0


if __name__ == "__main__":
    sys.exit(main())
