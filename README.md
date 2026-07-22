# Mersal HBMP — Healthcare Benefit Management Platform

A service-oriented **benefit-administration + EMR** platform for refugee beneficiaries of the Mersal Foundation. Open-source, on-prem-first, cloud-ready, $0 licensing.

> **Start here:** the root [`CLAUDE.md`](CLAUDE.md) carries the stack, conventions, security/audit/a11y rules, and Definition of Done that govern every change. The full design set is in [`HBMP-Design/`](HBMP-Design/) and the phased build prompts in [`HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`](HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md).

## What this is
A reusable core — Beneficiaries, Eligibility, Coverage/Policy, Provider Network, Authorizations, Orders, Prescriptions — with clinical and operational domains on top, built as independently deployable **.NET 8** microservices behind a **Kong** gateway, with a **React + TypeScript** role-portal frontend. Everything self-hostable on one server → k3s → any cloud, same containers.

## Repository layout
```
/CLAUDE.md          # conventions (auto-loaded every session)
/docs/              # ADRs (docs/adr), runbooks, security, compliance
/infra/             # IaC: compose (Tier 1) · tofu · ansible · helm  (ADR-0002/0003/0004)
/libs/              # shared: auth · audit-client · authz · events · contracts · testing
/services/          # one microservice per bounded context
/apps/web           # React role portals   /apps/design-system  # tokens + components
/tools/             # data loaders (master-data ingestion)
/HBMP-Design/       # design set (0A–35) + prompts + 20 custom skills + tier1 compose
/Master Lists, /Raw Files  # real reference data (ICD-10, CPT, ATC drugs) ingested in phase 0b
```

## Build & dev prerequisites
- **.NET 8 SDK** — installed user-local at `~/.dotnet` on this machine. Use `./dotnet.sh <cmd>` (wraps the local SDK), or add `~/.dotnet` to `PATH` with `DOTNET_ROOT=~/.dotnet`.
- **Docker + Compose** — required to run the Tier 1 infra (`infra/compose`). Install needs root; see [`docs/adr/0003`](docs/adr/0003-deployment-tier-strategy.md).
- **Node 20 / npm** — for the frontend (`apps/`) and commit-lint.
- **PostgreSQL 17** available locally; canonical dev DB is the Compose `postgres:16`.

### Run the infrastructure (Tier 1)
```bash
cd infra/compose && cp .env.example .env   # edit every value
docker compose up -d
```

### Build & test the backend
```bash
./dotnet.sh build      # once the first service exists
./dotnet.sh test
```

## How the build proceeds
Phase by phase, in dependency order, **one prompt ≈ one reviewable PR** — see the master list. Current status is tracked in [`docs/BUILD-STATUS.md`](docs/BUILD-STATUS.md).

## Custom skills
20 Mersal-specific Claude Code skills are installed under `.claude/skills/` (symlinked from `HBMP-Design/claude-code-skills/`). Always-on: `mersal-platform-architect`, `refugee-healthcare-management`. Activate the phase-specific skills listed in each phase prompt.

## Non-negotiable invariants
1. Order-line consume / dispense is **atomic, idempotent, duplicate-proof**.
2. **Minimum-necessary** data per role, enforced at **row and field** level in code.
3. **Immutable, hash-chained audit** on every mutation/decision/consume/dispense/export/PHI-read.
4. **WCAG 2.2 AA + Arabic RTL** on every UI story.
5. **Soft delete + history**, never hard delete of clinical/benefit data.

## License
Intended $0-licensing / open-source stack. Project licensing TBD by Mersal Foundation.
