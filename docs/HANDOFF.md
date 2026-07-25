# Mersal HBMP — Continuation / Handoff (read this first)

**Purpose:** everything a new engineer/LLM needs to continue building the Mersal HBMP exactly as started. Pairs with `docs/BUILD-STATUS.md` (the phase-by-phase checklist) and the prompt library at `HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`.

Last updated: 2026-07-25 (Phase 4 complete).

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
> **UPDATE 2026-07-25: Phase 4 (clinical EMR + investigation orders + e-prescriptions) is now COMPLETE** (4.1 emr clinical docs `8de1c25`, 4.2 orders-service `a4d65bc`, 4.3 pharmacy-service `ed42f21` — **330 tests green**). The doctor-facing R2 half of the platform. **Treating-relationship (US-030) is the spine**: a row-level check feeds the shared authorization engine's `treating-relationship` ABAC condition; the truth lives in **emr-service** (encounters) and is exposed as a min-necessary boolean probe `GET /api/v1/treating-relationship?beneficiaryId=` that orders/pharmacy call (caller token forwarded) so all three enforce the SAME rule. Non-treating clinician → 403 + audited attempted-PHI-access. **4.1 emr-service** (fills the Phase-2.3 stub): SOAP/Progress/Nursing notes (**sign-lock** → immutable, corrections via **addendum** only, unsigned editable by author), ICD-10 diagnoses (validated vs masterdata), range-validated vitals (+optional LOINC), allergies + medication history; all codes validated against masterdata **fail-closed**; soft-delete only; **FHIR R4 read projection** (Encounter/Condition/Observation/AllergyIntolerance/MedicationStatement). New `EmrPolicies` bundle: doctor/nurse read+write need treating; medical-approval team reads via distinct `emr:read-oversight` action (no treating); reception/labs/pharmacy default-denied clinical. Migration `0005_clinical.sql`. **4.2 orders-service** (NEW, `orders` schema, :port TBD): `POST /investigation-orders` — treating-gated, every CPT/LOINC/LOCAL line validated vs masterdata (422 on unknown), config-driven `OrderRoutingPolicy` routes high-cost/gated → `Requested→PendingApproval` (`OrderPendingApproval`) else auto-activates → `Active` (`OrderActivated`); `OrderCreated`+routed event via **outbox** in the same tx; idempotent create; cancel transition-guarded; `order_line` carries the consume accumulator `CHECK (0≤consumed≤ordered)` for phase-5. `OrdersPolicies` = ProviderPolicies (phase-5 provider reads) + order-create. Migration `0001_orders.sql`. **4.3 pharmacy-service** (NEW, `pharmacy` schema): `POST /prescriptions` — treating-gated, drug_id validated vs masterdata, **advisory (non-blocking) interaction + allergy alerts** (masterdata `/drug-interactions/check-by-ids` + `/allergies/check-by-ids`; allergies pulled from emr `GET /beneficiaries/{id}/allergies`) recorded with acknowledgement; **Draft→Submitted** then config-driven `RxRoutingPolicy` keeps expensive/gated Submitted (dispensable only once Approved) else auto-approves; `RxCreated`/`RxSubmitted`(/`RxApproved`) via outbox; `prescription_line` accumulator `CHECK (0≤dispensed≤prescribed)` for phase-6. `POST /referrals` → Requested + `ReferralRequested` (US-034). `PharmacyPolicies`; pharmacist cannot prescribe. Migration `0001_pharmacy.sql`. masterdata gained by-id existence + by-id screening endpoints. **emr/orders/pharmacy migrations applied to host PG (:55432).** **Next is Phase 5** (lab/imaging fulfillment: provider order queue + **atomic idempotent consume** + result upload) and **Phase 6** (pharmacy dispensing) — both consume what Phase 4 produced; they can run in parallel, per `phase-5-*.md` / `phase-6-*.md`.

> **UPDATE 2026-07-25: Phase 3 (appointments) is now COMPLETE** (3.1 booking `a39b647`, 3.2 reschedule/cancel/no-show `19c0669`, 3.3 queue + reminders `8717b70` — **242 tests green**; emr-service 52). Scheduling was added to **emr-service** (`emr` schema), not a new service. **3.1** — `provider_availability` → materialized `appointment_slot`s; `POST /appointments` books with a **hard no-double-book** guarantee: the booking tx locks the slot `FOR UPDATE` and the `ux_appointment_active_slot` **partial-unique index** (WHERE status IN Booked/CheckedIn) is the backstop — the losing racer gets 23505→409. Proven by a live 12-parallel-booker concurrency test. Persisted `appointment.status` is EXACTLY {Booked,CheckedIn,Completed,NoShow,Cancelled}; Requested/Waitlisted live on `waitlist_entry`. Referral bookings link REF-* (emit `ReferralScheduled`); follow-ups link an encounter; no-slot → next-slots or a `202` waitlist. **3.2** — `/reschedule` (atomic release-old + acquire-new in ONE tx), `/cancel`, `/no-show` (guarded: window passed AND still Booked; sets `no_show` reporting flag; frees slot for backfill; repeat no-shows ≥3 → `BeneficiaryNoShowThresholdReached` for Case Manager). Slot "release" is implicit (the partial-unique index only counts active holds). All mutations honor `Idempotency-Key` (→ `emr.processed_request` ledger) + `If-Match` (xmin ETag from GET → 412); illegal moves are an audited 409 `TransitionDenied`. **3.3** — reception walk-in queue per (location,provider,doctor): `/check-in` (Booked→CheckedIn + enqueue min-necessary ticket), `GET /queues` (position/memberNo/name/type/wait only — no EMR, reflection-guarded), `call-next`/`requeue`/`remove`/`complete`; cancel/no-show auto-remove tickets. **Reminders hook**: `IReminderChannel` + `ReminderDispatcher` (preferred-channel selection, in-app fallback) — in-app live (outbox→`notification.events`), SMS/WhatsApp are stubs; a Booked reminder fires on booking + `POST /appointments/reminders/run`. emr migrations `0002`–`0004` applied to host PG (:55432). **Next is Phase 4** (clinical EMR: SOAP/orders/e-prescriptions — fills the rest of the emr stub) per `phase-4-*.md`. Everything below is historical reference.

> **UPDATE 2026-07-23: Phase 2b (provider network & isolation) is now COMPLETE** (2b.1 provider-service `f54e08b`, 2b.2 onboarding `7f2bd27`, 2b.3 isolation `78bb6da` — **201 tests green**). New `provider-service` (:8097) owning the `provider` schema — the R2 fulfillment backbone for phases 5/6. Providers/locations/contracts/service-lines/credentials CRUD + `GET /capabilities` (routable only from an Active provider under an in-effect contract; `agreed_price` masked without `provider:finance`; CPT validated vs masterdata); contract effective-range GiST exclusion + primary-location partial-unique. **Network Team onboarding** state machine (`OnboardingWorkflow`): guarded `/activate` (blocked without primary location + valid mandatory credentials + active contract), `/suspend` + dual-controlled `/terminate` (revoke all provider users, stop routing), provider-user provisioning with **SoD** (`ProviderUserRules`: no self-elevation, no clinical roles), credential-expiry reminder sweep. **Provider isolation in depth**: token provider_id check, reusable `ProviderPolicies` ABAC bundle (imported by orders/pharmacy later), **PostgreSQL RLS** (`0003` ENABLE+FORCE + session-GUC `RlsConnectionInterceptor`), min-necessary `ProviderBoundaryPatient`, provider-scoped metrics. **KEY OPS FINDING:** RLS only bites under a **non-superuser `NOBYPASSRLS`** role — the app must connect as `hbmp_app` (`0004_app_role.sql`); the default `hbmp` role is superuser and silently bypasses RLS (proven both ways). Provider migrations `0001`–`0004` already applied to host PG (:55432); `hbmp_app` role created (password `Dev_AppPass_2026!` dev-only, set out of band). **Next is Phase 3** (appointments) per `phase-3-*.md`. Everything below is historical reference.
>
> **UPDATE 2026-07-23: Phase 2 is now COMPLETE** (2.1 eligibility-service `df91758`, 2.2 reception search `4d930ce`, 2.3 emr visit-gating stub `15ea73e` — all committed, **169 tests green**). New services: `eligibility` (:8095 — decision engine {Eligible|Ineligible|NeedsAuthorization} from member status + coverage + remaining limits; Valkey cache keyed (beneficiaryId,category) TTL 15m + in-memory fallback; `EventConsumer`/`ProjectionUpdater` consume `patient.events`+`policy.events` idempotently and invalidate; min-necessary reception search `GET /reception/search` with an authz reflection test proving no EMR leak; `GET /eligibility/members/{id}/status` for the gate) and `emr` (:8096 — `POST /encounters` status-gated: Active→ENC-* shell + clinician queue + EncounterStarted/ApptCheckedIn, else 422 guidance, idempotent). Enriched patient `BeneficiaryRegistered`/`Activated` + policy `CoverageChanged` payloads with min-necessary fields; **turned on the outbox relay** in patient/policy/eligibility/emr (also fixes audit delivery in dev) with a **lazy/resilient** `RabbitMqEventPublisher` (unreachable broker degrades, no startup crash). **Before a live demo, apply the two new migrations** to host Postgres (:55432): `services/eligibility/Infrastructure/Migrations/0001_eligibility.sql` and `services/emr/Infrastructure/Migrations/0001_emr.sql`. **Next is Phase 2b** (provider network + isolation) per `phase-2b-provider-network.md`. The Phase-1 sub-sections below are historical reference.
>
> **UPDATE 2026-07-23 (earlier): Phase 1 COMPLETE** (1.1 patient, 1.2 policy, 1.3 document, 1.4 registration→activation — 128 tests green, activation proven live: register→approve→MRS-M-2026-000001 + BeneficiaryActivated). The 1.3/1.4 sub-sections below are DONE; keep them for reference.

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
