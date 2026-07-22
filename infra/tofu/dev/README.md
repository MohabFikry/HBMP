# OpenTofu — `dev` environment

Provisions the dev environment substrate (Tier 1/2). For local Tier 1, the actual runtime is `infra/compose`; this stack exists so `tofu validate`/`plan` is a real CI gate and so the same code scales to a single-node k3s and cloud.

## Conventions (ADR-0002)
- Backend: remote (S3-compatible / MinIO). **Never** commit `*.tfstate`. Backend config is provided out-of-band (not committed) per CLAUDE.md.
- No hardcoded secrets — values come from OpenBao / CI variables.
- `tofu init -backend=false && tofu validate` must pass (CI `iac-validate`).

## Status
Skeleton. Real resources (network, k3s node, DNS, MinIO backend) land as Tier 2 is provisioned. Add `.tf` files here; the CI job auto-discovers and validates them.
