# ADR-0000: Architecture Decision Record process

- Status: Accepted
- Date: 2026-07-22
- Deciders: Platform architecture

## Context
The Mersal HBMP is built phase by phase (see `HBMP-Design/claude-code-prompts/00-MASTER-PROMPT-LIST.md`). Every real decision must be recorded so later phases and reviewers understand *why*, not just *what*. The root `CLAUDE.md` mandates an ADR per real decision.

## Decision
We keep lightweight ADRs in `/docs/adr`, numbered `NNNN-title.md`, using the format: Context → Decision → Consequences → Alternatives. Status ∈ {Proposed, Accepted, Superseded by ADR-XXXX, Deprecated}. One decision per file; supersede rather than edit history.

## Consequences
- Decisions are traceable and reviewable in PRs.
- ADRs are referenced from service READMEs and phase commits.

## Alternatives considered
- A single running decision log (rejected: poor diffability, merge conflicts).
