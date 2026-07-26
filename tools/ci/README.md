# tools/ci — backend CI helpers (Phase 16.8, finding H7)

Used by `.github/workflows/backend-ci.yml`; each is runnable locally against a scratch DB.

| Script | What it does |
|--------|--------------|
| `apply-migrations.sh` | Provisions the `hbmp_app` NOBYPASSRLS role, then applies every service's hand-authored `services/*/Infrastructure/Migrations/*.sql` (filename order) to the target Postgres. Idempotent where the migrations are. |
| `print-test-db-env.sh` | Emits the `*_TEST_DB` / `*_TEST_DB_OWNER` / `*_TEST_DB_APP` / `EVENTS_TEST_DB` `KEY=VALUE` lines that make the env-gated integration + RLS suites **run** instead of skip. Redirect into `$GITHUB_ENV`. |
| `coverage-gate.sh` | Aggregates `**/coverage.cobertura.xml` and enforces a domain-coverage floor (`COVERAGE_MIN_DOMAIN`, default 55%; target is 80% — ratchet up). Overall coverage is printed, not gated. |
| `generate-openapi.sh` | Generates every service's OpenAPI/Swagger doc via the Swashbuckle CLI and fails if any spec can't be produced (catches broken Swagger config / duplicate routes). Most services connect lazily (dummy conn strings suffice); a caller-set `ConnectionStrings__<Key>` is kept for the few that migrate at startup (audit). Needs the solution built + `dotnet tool restore`. Pass `DOTNET=./dotnet.sh` to run locally. |

## Run the DB suite locally against a scratch DB

```bash
export PGHOST=localhost PGPORT=55432 PGUSER=hbmp PGPASSWORD='<owner-pw>'
psql -d postgres -c "CREATE DATABASE hbmp_ci_verify OWNER hbmp;"

PGDATABASE=hbmp_ci_verify HBMP_APP_PASSWORD='app_pw' bash tools/ci/apply-migrations.sh

export PGDATABASE=hbmp_ci_verify OWNER_PASSWORD="$PGPASSWORD" APP_PASSWORD='app_pw'
while IFS='=' read -r k v; do export "$k=$v"; done < <(bash tools/ci/print-test-db-env.sh)
./dotnet.sh test HbmpPlatform.sln -c Release
```

## Notes
- The CI `postgres` service uses **throwaway** credentials — never the real dev secrets (gitignored
  `infra/compose/.env`; prod uses OpenBao).
- The whole solution's integration + RLS suites pass against a **clean** Postgres (no master-data seed
  required — the suites self-seed and self-clean by scope tag).
