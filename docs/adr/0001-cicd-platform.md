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

## Consequences
- One platform to operate/back up; Harbor must be provisioned alongside (Tier 2/3) — for Tier 1 dev, images build locally and scanning runs in-pipeline.
- CI runners execute the user-local .NET 8 SDK image; see `dotnet.sh` / `global.json`.

## Alternatives considered
- **Gitea + Woodpecker + Harbor** — lighter to self-host but splits SCM/CI/review across products; kept as the documented fallback.
- **GitHub Actions** — not on-prem-first / $0 for private self-hosting; only relevant in a funded cloud Tier 3.
