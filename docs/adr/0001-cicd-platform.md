# ADR-0001: CI/CD platform — GitLab CE + Harbor

- Status: Accepted
- Date: 2026-07-22
- Deciders: Platform architecture
- Phase: 0 (0.1)

## Context
Phase 0.1 requires a self-hosted, $0-licensing CI/CD pipeline that runs the required gates (build, unit+contract tests with ≥80% coverage on domain projects, SAST, dependency+image scan, a11y hook, IaC validation) and pushes images to a self-hosted registry. `0C-OPEN-SOURCE-STACK.md` names **GitLab CE** or **Gitea + Woodpecker** as the two options, with **Harbor** as the registry and **Trivy** as the scanner.

## Decision
Adopt **GitLab CE** as the CI/CD platform and **Harbor** as the container registry.

Rationale:
- GitLab CE is a single, self-hostable ($0) product providing SCM + CI + container-scanning hooks + merge-request review in one place — fewer moving parts for a charity ops team than Gitea + a separate Woodpecker CI.
- First-class `.gitlab-ci.yml` pipeline-as-code, protected branches, required pipelines, and merge-request approval gates map directly onto the phase governance gates.
- Harbor gives us image signing (Cosign), Trivy-backed scanning, and RBAC on the registry, and is the `0C` default.
- The pipeline is portable: jobs are plain shell + containers, so a later move to Gitea + Woodpecker (or GitHub Actions in a cloud tier) is mechanical.

The canonical pipeline lives in `.gitlab-ci.yml` (+ includes under `.gitlab/ci/`). Conventional Commits are enforced by a `commit-lint` job.

## Amendment — 2026-07-27 (Phase 18.E1, audit R2 Q1): the executing pipeline is GitHub Actions; the GATES are shared scripts

**What happened.** Every gate that actually protects this repo was built in `.github/workflows/*.yml`:
migration expand/contract, the two-role RLS isolation suites, the DPIA gate, coverage, OpenAPI generation.
`.gitlab-ci.yml` was written in Phase 0 against an empty scaffold and never caught up — by Phase 18 its test
job printed `Coverage threshold: 80%` and enforced nothing.

**Why that mattered more than the duplication.** Two pipelines both claiming to gate the same repository,
one of them describing a check it does not perform, is worse than either alone. Someone asking "is coverage
gated, and at what number?" got a different answer depending on which file they opened — and the reassuring
answer was the false one.

**Decision.** The choice of GitLab CE stands: it remains the on-prem, $0, self-hosted target ADR-0001 chose
for a charity operations team, and nothing here reverses that. What changes is where a gate LIVES:

- Every structural gate is implemented once, in `tools/ci/*` (plain Python/bash, no runner-specific syntax).
- **Both** pipelines invoke those scripts. Neither reimplements a check.
- GitHub Actions is the pipeline that runs today, on the current hosting. `.gitlab-ci.yml` is the on-prem
  port and is kept executable so the migration is a runner change, not a rewrite.

The consequence worth stating plainly: a gate can no longer be strengthened in one pipeline and left weak in
the other, because there is only one implementation of it. That property — not which YAML dialect runs — is
what the split-brain was costing.

## Consequences
- One platform to operate/back up; Harbor must be provisioned alongside (Tier 2/3) — for Tier 1 dev, images build locally and scanning runs in-pipeline.
- CI runners execute the user-local .NET 8 SDK image; see `dotnet.sh` / `global.json`.

## Alternatives considered
- **Gitea + Woodpecker + Harbor** — lighter to self-host but splits SCM/CI/review across products; kept as the documented fallback.
- **GitHub Actions** — not on-prem-first / $0 for private self-hosting; only relevant in a funded cloud Tier 3.
