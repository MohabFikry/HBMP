# Mersal HBMP — Continuation / Handoff (read this first)

**Purpose:** everything a new engineer/LLM needs to continue building the Mersal HBMP exactly as started. Pairs with `docs/BUILD-STATUS.md` (the phase-by-phase checklist) and the prompt library at `HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`.

Last updated: 2026-07-22.

---

## 1. What this project is
A phase-by-phase production build of the Mersal Healthcare Benefit Management Platform (.NET 8 microservices + React later, open-source on-prem-first stack). The **design set** is in `HBMP-Design/` (docs 0A–35). The **build prompts** are in `HBMP-Design/claude-code-prompts/` — one sub-prompt ≈ one commit/PR. Root `CLAUDE.md` carries the conventions (auto-loaded). 20 skills in `.claude/skills/`.

**Golden rule:** read the design docs a prompt names *before* coding; if reality diverges, flag it in the commit, don't silently deviate.

## 2. How to build/test (this machine)
- **.NET 8 SDK is user-local** at `~/.dotnet`. Always use **`./dotnet.sh`** (wrapper) — e.g. `./dotnet.sh build`, `./dotnet.sh test HbmpPlatform.sln`.
- Node 20, npm, git, `psql 17` client present. **No** system .NET.
- Full suite: `./dotnet.sh test HbmpPlatform.sln -c Release`. **107 tests green** as of last commit (document-service tests not written yet).

## 3. Progress (commits, newest last)
```
8208331 chore(platform): scaffold monorepo, CI/CD, dev IaC (0.1)
e4ec9dd feat(auth): libs/auth + Keycloak realm-as-code (0.2)
6be102a feat(audit): audit spine — libs/audit-client + audit-service (0.3)
f4d3624 feat(authz): libs/authz RBAC+ABAC row+field + break-glass (0.4)
958a216 feat(platform): libs/events outbox + hello-service slice (0.5)  ← Phase 0 COMPLETE
f754520 feat(masterdata): masterdata-service + real ICD/CPT/ATC/drug ingest (0b)  ← Phase 0b COMPLETE
23cab1b chore(infra): turnkey Tier1 up.sh + seed-masterdata.sh
761de68 feat(patient): patient-service registration + dedup (1.1)
b257cd4 feat(policy): policy-service coverage/limits/reset (1.2)
```
**UNCOMMITTED work in the tree right now** (finish + commit these):
- `services/document/**` — Phase **1.3 document-service, PARTIAL**: Domain done (`Entities`, `UploadValidation`, `UploadPipeline` with `IMalwareScanner`/`IBlobStore` ports), Infrastructure `DocumentDbContext` + `0001_document_schema.sql` done. **NOT done:** `Api/Program.cs` (still hello-world), Infrastructure ClamAV scanner + MinIO blob store + DI, Tests. Projects ARE added to the solution.
- Master-data snake_case fix: added `EFCore.NamingConventions` (Directory.Packages.props), masterdata Infra csproj refs it, masterdata Api + loader call `.UseSnakeCaseNamingConvention()`, and `MasterDataDbContext` maps `Icd11Map → icd11_map` explicitly (convention renders digits wrong).
- `infra/compose/compose.yaml`: postgres host port changed to **55432** (see §5); patient(:8092)/policy(:8093)/masterdata(:8091)/audit/hello services added.
- `infra/compose/seed-masterdata.sh`: uses port 55432.

## 4. Live stack state (as left running — ephemeral)
- **Docker** installed (29.1.3). Socket access was granted with `sudo chmod 666 /var/run/docker.sock` — **this resets when the Docker daemon restarts**; re-run it (or `sudo usermod -aG docker $USER` + new login) to use docker again.
- **Running containers:** postgres, keycloak, kong, minio, rabbitmq. **Not pulled/started:** nats, valkey, opensearch, openbao, clamav, prometheus, grafana, loki, tempo (Docker Hub was flaky — pull them with retries; see §6).
- **Real master data IS loaded** into the `hbmp` DB `masterdata` schema: 16,751 icd_code · 10,810 cpt_code · 2,150 atc_class · 25,063 drug (15,311 ATC-linked). Verified via psql.
- `masterdata-service` was run on the host at `http://localhost:5072` (background `dotnet run`; **dies with the session** — restart with §7).
- **Keycloak realm `mersal` imports fine** (OIDC config serves the custom scopes). **KNOWN ISSUE:** the master-realm bootstrap admin login fails ("Invalid user credentials"/"user_not_found") even after recreating the keycloak DB — so we could NOT mint a token to demo an authenticated 200. Unauthenticated calls correctly return 401 (auth is enforced). **To fix/get a token** (needed to exercise protected endpoints live): investigate KC 25 bootstrap (`KC_BOOTSTRAP_ADMIN_USERNAME/PASSWORD` in compose env) — possibly use `docker exec … /opt/keycloak/bin/kc.sh bootstrap-admin user` or set a fresh admin, then create a confidential client `hbmp-demo` (serviceAccounts + directAccessGrants + an `oidc-audience-mapper` for `hbmp-api`) and use client_credentials/password grant. The API validates `aud=hbmp-api`, so tokens MUST carry that audience.

## 5. Critical gotchas (will bite you)
1. **Postgres host port is 55432**, not 5432 (local PG16 owns 5432). Inside compose, services connect to hostname `postgres:5432`. Host tools (loader/psql) use `localhost:55432`. `.env` (gitignored) is in `infra/compose/.env` with dev secrets.
2. **.NET 8, not 9.** Do NOT use `ToHashSetAsync` (use `(await q.ToListAsync()).ToHashSet()`), `Convert.ToHexStringLower` (use `Convert.ToHexString(x).ToLowerInvariant()`), or `System.Threading.Lock` (use `object`).
3. **Analyzers = errors** (warnings-as-errors). Repo-wide `NoWarn`: CA1848/CA1859/CA1725/CA1716. Test projects also relax CA1707 etc. Records declared in a top-level-statements `Program.cs` trip CA1050 → put them in a namespaced file.
4. **NuGetAudit fails the build on HIGH/CRITICAL** advisories (matches the Trivy gate). Pin patched transitive versions in `Directory.Packages.props` when it complains.
5. **EF column naming:** patient/policy/audit map every column explicitly with `HasColumnName` (snake_case). masterdata uses `.UseSnakeCaseNamingConvention()`. Pick one per new service and be consistent; watch digit-boundary props (e.g. `Icd11Map`).
6. **Central Package Management:** every `PackageReference` has NO `Version=` — versions live in `Directory.Packages.props`. Classlibs that need ASP.NET types use `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, not packages.
7. Nested `Tests/` under a lib project: exclude with `<Compile Remove="Tests/**/*.cs" />` in the lib csproj (services keep Tests as a SEPARATE sibling project — no exclude needed).
8. Background `docker compose up` of all images aborts if any one image fails to pull. **Pull sequentially** with per-image retries (Docker Hub TLS timeouts are common here).

## 6. Bring the stack fully up
```bash
# 0. (after any docker daemon restart) re-enable socket access:
#    sudo chmod 666 /var/run/docker.sock
# 1. pull the not-yet-pulled images (retry loop; do NOT background — they got SIGKILLed):
for img in nats:2.10 valkey/valkey:8 opensearchproject/opensearch:2.15.0 openbao/openbao:latest \
           clamav/clamav:latest prom/prometheus:latest grafana/loki:3.1.0 grafana/tempo:latest grafana/grafana:latest; do
  for a in $(seq 1 8); do docker image inspect "$img" >/dev/null 2>&1 && break; docker pull -q "$img" && break; sleep 4; done
done
# 2. start everything:
cd infra/compose && docker compose up -d
# 3. build + start the app-service images (or run on host, see §7):
docker compose up -d --build audit-service masterdata-service patient-service policy-service hello-service
```
Endpoints: Keycloak :8080, Kong :8000, Grafana :3000, MinIO console :9001, RabbitMQ :15672, masterdata :8091, hello :8090, patient :8092, policy :8093.

## 7. Run a service on the host (fast, no image build)
```bash
set -a; . infra/compose/.env; set +a
export ConnectionStrings__MasterData="Host=localhost;Port=55432;Database=hbmp;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
export Auth__Authority="http://localhost:8080/realms/mersal" Auth__Audience="hbmp-api" Auth__RequireHttpsMetadata="false"
export ASPNETCORE_URLS="http://localhost:8091" OTEL_SDK_DISABLED="true"
./dotnet.sh run --project services/masterdata/Api -c Release   # (launchSettings may override the port → check the log for "Now listening on")
```
Re-seed master data any time (idempotent): `bash infra/compose/seed-masterdata.sh`.

## 8. The reusable service pattern (copy `services/hello` or `services/patient`)
Per `CLAUDE.md`: `Api/ Domain/ Infrastructure/ Tests/` + `Dockerfile` + `README.md`. `Program.cs` wires:
```csharp
builder.Services.AddHbmpAuthentication(builder.Configuration);   // libs/auth  (JWT+MFA)
builder.Services.AddHbmpAuditClient("<name>-service");           // libs/audit-client
builder.Services.AddHbmpAuthorization();                         // libs/authz (RBAC+ABAC row+field)
builder.Services.AddHbmpEvents(builder.Configuration, useInMemory: true); // libs/events outbox
// + AddDbContext(UseNpgsql...), OpenTelemetry, AddProblemDetails, Swagger
app.UseAuthentication(); app.UseAuthorization();
// endpoints: .RequireAuthorization(HbmpPolicies.Scope("<scope>")); authorize via IAuthorizationEngine;
// mutate in a txn that also EmitAsync(audit) + outbox.EnqueueAsync(event). RFC7807 problem+json on failure.
```
Then: add 4 projects to `HbmpPlatform.sln`, add a Dockerfile, a compose entry, a service README, and tick `docs/BUILD-STATUS.md`. Schema migrations are hand-authored SQL under `Infrastructure/Migrations/*.sql` (partition/grants/RLS/triggers need raw SQL); apply on dev startup or via a small runner.

## 9. IMMEDIATE next tasks (in order)
> **UPDATE 2026-07-23: Phase 1 is now COMPLETE** (1.1 patient, 1.2 policy, 1.3 document, 1.4 registration→activation — all committed, 128 tests green, activation proven live: register→approve→MRS-M-2026-000001 + BeneficiaryActivated). **Next is Phase 2** (eligibility-service + reception min-necessary search + visit gating) per phase-2-eligibility-reception.md. The 1.3/1.4 sub-sections below are DONE; keep them for reference.

### 9a. FINISH Phase 1.3 — document-service (started, uncommitted)
Prompt: `HBMP-Design/claude-code-prompts/phase-1-registration-policy.md` §1.3 (US-002). Design: `15-database-erd.md §12`.
- **Infrastructure:** `ClamAvScanner : IMalwareScanner` (TCP to ClamAV `clamd` on `clamav:3310`, INSTREAM; fail-closed), `MinioBlobStore : IBlobStore` (AWSSDK.S3 → MinIO, private bucket `beneficiary-documents`), `DependencyInjection.AddDocumentInfrastructure` (DbContext + scanner + blob store).
- **Api/Program.cs:** `POST /api/v1/beneficiaries/{id}/documents` multipart upload → `DocumentUploadService.UploadAsync` → 201 (stored, versioned, checksum+uploader) / 400 (rejected type/size, reason) / 422 (quarantined malware). Audit every upload/attach/reject; scope `document:write`. `GET` list (metadata only, min-necessary).
- **Tests:** validation matrix (oversize/bad-type rejected), malware-positive → Quarantined (fake scanner), clean → Stored + version + checksum. Use fakes for `IMalwareScanner`/`IBlobStore` (no Docker needed).
- Dockerfile + compose entry (:8094) + README + BUILD-STATUS. Commit `feat(document): ... (phase 1.3)`.

### 9b. Phase 1.4 — registration workflow + activation (US-003/US-004)
In `patient-service`. Model a `registration` aggregate (status Pending|InfoRequested|Rejected|Active) distinct from beneficiary.status. Endpoints: `POST /registrations` (Idempotency-Key), PATCH step data (If-Match→412), `POST /registrations/{id}/submit`, `POST /registrations/{id}/decision {Approve|RequestInfo|Reject, notes}`. **Approve (transactional):** guard = documents verified AND coverage bound → set beneficiary.status Active, issue `MRS-M-YYYY-NNNNNN` via `MemberNoIssuer`, write MemberNo identifier, **emit `BeneficiaryActivated`** (phase-2 eligibility consumes it). Reject → reason mandatory. Also expose the lifecycle transitions (suspend/expire/block/reactivate — `BeneficiaryLifecycle` already exists) with mandatory reasons + PATCH If-Match. Tests: full register→submit→approve→activate path asserts MemberNo + event; illegal-transition 409; idempotent replay. Commit → **Phase 1 COMPLETE**.

### 9c. Then continue in dependency order (see BUILD-STATUS + master list)
`Phase 2` (eligibility engine + reception min-necessary search + visit gating) → `2b` (provider network + isolation) → `3` (appointments) → `4` (EMR/orders/prescriptions) → `5`/`6` (lab-imaging consume / pharmacy dispense — the atomic-idempotent invariant) → `7` (approvals) → `8` (notify+reporting) → `8b` (admin) → `9` (frontend) → `10` (case+finance) → `11` (hardening) → `12` (migration/go-live) → `13` (interop). `8b`+`9` run alongside.

## 10. Non-negotiable invariants (every phase)
1. Order/prescription consume-dispense is **atomic, idempotent, duplicate-proof** (unique index + optimistic version + Idempotency-Key). 2. **Minimum-necessary** row+field, in code (`RowScope`/`FieldProjector`). 3. **Immutable hash-chained audit** on every mutation/decision/consume/dispense/export/PHI-read. 4. **WCAG 2.2 AA + Arabic RTL** on every UI. 5. **Soft-delete + history**, never hard delete clinical/benefit data. 6. Tokens validated at gateway AND service; MFA for protected scopes.
