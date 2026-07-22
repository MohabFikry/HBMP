# ADR-0002: IaC tooling — OpenTofu + Ansible + Helm

- Status: Accepted
- Date: 2026-07-22
- Deciders: Platform architecture
- Phase: 0 (0.1)

## Context
The platform is on-prem-first, cloud-ready, $0 licensing. We need infrastructure-as-code that provisions cloud/VM resources, configures hosts, and deploys the workload identically across deployment tiers, with a CI `validate + plan` gate and no plaintext secrets in git.

## Decision
Use **OpenTofu** (provisioning), **Ansible** (host/config), and **Helm** (Kubernetes packaging) — the `0C-OPEN-SOURCE-STACK.md` default.

- **OpenTofu** (MPL-2.0 fork of Terraform) provisions infra (VMs/networks/DNS, and in cloud tiers managed resources). State uses a remote backend (S3-compatible / MinIO) — never committed. `tofu validate` + `tofu plan` are required CI gates.
- **Ansible** configures hosts (k3s install, LUKS, OS hardening, pgBackRest/restic agents).
- **Helm** charts package each service and the platform, targeting k3s (Tier 2) and cloud (Tier 3) unchanged; the same images run under Docker Compose for Tier 1.
- Secrets: **OpenBao** at runtime; **SOPS**-encrypted values in git for config-as-code. No plaintext secrets committed.

Layout under `/infra`: `tofu/` (per-env stacks), `ansible/` (roles/playbooks), `helm/` (umbrella + per-service charts), `compose/` (Tier 1 single-node).

## Consequences
- Three tools to learn, but each is best-in-class and $0; one substrate across tiers.
- CI needs OpenTofu + Helm + ansible-lint available to runners.

## Alternatives considered
- **Terraform** — license (BUSL) conflicts with the $0/open-source mandate → OpenTofu chosen.
- **Pulumi / CDK** — language-based IaC; heavier, less on-prem-charity-friendly.
- **Kustomize instead of Helm** — Helm's packaging/versioning/rollback fits progressive delivery better (phase 11/12).
