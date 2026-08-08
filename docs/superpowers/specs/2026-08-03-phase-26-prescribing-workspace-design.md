# Phase 26 — Prescribing workspace & clinical validation: design

**Date:** 2026-08-03
**Design authority:** [`HBMP-Design/43-approval-engine-and-prescribing-support.md`](../../../HBMP-Design/43-approval-engine-and-prescribing-support.md)
**Build prompt:** [`phase-26-prescribing-workspace.md`](../../../HBMP-Design/claude-code-prompts/phase-26-prescribing-workspace.md)

This records the decisions taken before implementation, including four that doc 43 could not have
anticipated because they only became visible once the source workbook was actually inspected.

---

## 1. The safety position

Two rules dominate and are not negotiable:

1. **Benefit rules may block. Clinical checks may only warn** — acknowledge with a reason to proceed.
2. **"Check unavailable" is never rendered as "OK".** Five per-line states, four cues each.

Today `pharmacy/Api/HttpClients.cs:71,84,103` catches every transport error and returns "no alerts", so an
outage renders as a clean bill of health. That is the single most dangerous line in the prescribing path.

## 2. Architecture — make the bug unrepresentable

`libs/clinical-validation` is a new pure-domain library with no I/O, consumed by pharmacy (step 1) and, in
phase 27, by approvals (step 2) — one evaluator, not two that drift.

The central move is a type, not a rule:

```
Fetched<T> = Available(T, SourceName, SourceVersion, RetrievedAt)
           | Unavailable(reason)          ← carries no data payload
```

Ports return `Fetched<T>`, never a bare collection, and the validator is a pure function over already-fetched
snapshots. Because `Unavailable` holds no data, there is no value the validator *could* inspect to conclude
"no interactions found". The swallow cannot be reintroduced by a later well-meaning `catch`, because there is
no code path from a failed fetch to `Ok`.

```
CheckState = Ok | Warning | Blocked | NotChecked | Unavailable
CheckKind  = Indication | Interaction | Allergy | DoseDuration | Benefit
Finding    = kind, state, severity?, messageEn, messageAr,
             sourceName, sourceVersion, checkedAt, lineRef, drugId
```

`Blocked` is constructible only within the benefit namespace, so "a clinical check must never block" is a
compile-time property rather than a review comment.

A bounded per-port timeout maps to `Unavailable`. This is a small resilience addition to a repo that has
none, and it is justified by the invariant rather than by general robustness: a hanging masterdata call must
not hang the encounter, and must not be indistinguishable from a clean result.

## 3. What inspecting the workbook changed

`Master Lists/egyptian-drug-list_5.xlsx` — 5 sheets, 22,653 medicines. Full column mapping lives in
[`tools/masterdata-loader/README.md`](../../../tools/masterdata-loader/README.md). Four findings altered the
design:

**3.1 The indications are 3-character ICD categories.** All 874 distinct codes are categories (`E11`, `J01`);
not one is 4-character or dotted. But `masterdata.icd_code` stores dotted codes and `emr.diagnosis` records
the specific one. Equality comparison would report *"not a listed indication"* on virtually every
prescription — a warning that always fires is a warning clinicians learn to dismiss, which is the failure
mode this phase exists to avoid. **Both sides go through `MasterDataNormalize.IcdCategory` before
comparison.**

**3.2 `Z76` is a filler, not an indication.** The source drops it wherever a real indication exists, so it
only appears alone. **1,019 drugs (4.5%) carry it as their only code.** They load with zero indications and
report *"not checked"*. Storing `Z76` would let a product with no clinical data render as checked.

**3.3 The mapping is ATC-L4 keyed and is clinical judgement.** The workbook's own Notes sheet says so:
*"the ATC-to-ICD step itself is still clinical judgement, not a published dataset… Spot-validate a
stratified sample against EDA leaflets or FDA/EMA labels before this gates live claims."* This is why
`source` is mandatory on every indication row, is surfaced to the prescriber, and why an indication mismatch
may only ever warn.

**3.4 Three things the file cannot supply.** No Arabic trade name (`name_ar` stays null; the combobox falls
back to English). No UNHCR formulary — column AG is a header with no data, so phase 27's formulary must be
authored as a `benefit_list`. No dosing data, so the dose check reports *"no rule configured"*.

### Storage shape

Per-drug materialised rows, `drug_indication(drug_id, icd_code, source, source_release)` — 214,402 rows.
The alternative (store the 597-row ATC-L4 map, resolve at query time) was rejected: 3,458 drugs carry no ATC
code at all, so it needs a per-drug fallback anyway, and two code paths for one safety check is worse than
one wide table.

### Drug identity

`drug_id` is **derived** from the workbook's stable row id, not minted per load. It was `Guid.NewGuid()`,
which made id stability an accident of the trade-name string never drifting; any drift minted a fresh uuid
and orphaned the indications, interactions and prescription lines pointing at the old one. The upsert matches
`source_row_id`, then `drug_code`, and **keeps the existing uuid** so rows loaded from the earlier CSV are
adopted rather than duplicated. Nothing is hard-deleted.

## 4. The five per-line states

Answered and unanswered are different **visual classes**, not five peers:

| class | states | cues |
|---|---|---|
| answered | OK · Warning · Blocked | solid filled chip · teal circle / amber triangle / red square · word |
| **no answer** | Not checked · **Check unavailable** | **dashed border · hollow icon** · grey / slate · word |

A reader scanning a row sees "we have no answer here" before parsing colour or text, which survives
greyscale, colour-blindness and haste. Expanding a line shows the per-check breakdown — indication,
interaction, allergy, dose, benefit — each with its own state and its own source, version and timestamp.

## 5. Two-step validation

Step 1 (this phase) is advisory and **untrusted**. Step 2 (phase 27) re-evaluates server-side from current
state and ignores any client-supplied verdict. A submitted payload claiming everything is fine must not be
believed; the forged-payload test is pinned in the invariant registry.

## 6. Testing

Written first for the validation engine: table-driven `CheckKind × CheckState`; cross-line pair generation
(`n(n-1)/2`, deduped); **dependency-down yields `Unavailable`, never `Ok`**; **forged clean client payload is
ignored by the server**; `E11.9` diagnosis matches an `E11` indication; `Z76`-only yields `NotChecked`.

Loader tests cover idempotency, deterministic ids, the missing-column failure, and **unmatched-ICD
reporting** — which never fires against the real file (all 874 categories resolve) and is tested precisely
because a drug that silently loses its indications reports "not checked" forever with nothing to surface it.

## 7. Decisions taken during implementation

**`masterdata:read` (phase 26.1) vs the Phase 18.E2 position.** `MasterDataAuthzTests` argued that a bare
`RequireAuthorization()` was correct here and warned against "hardening" it. The scope was added anyway, and
the rationale rewritten in place rather than left contradicting the code. The old argument is still true as a
statement about clinicians — the grant is deliberately broad, to every role holding any scope — but the scope
makes reference-data reach a stated, revocable line in the role matrix, and forces a service or integration
token to ask for the catalogue instead of receiving it by default.

Two consequences surfaced in testing and are worth recording:

- Scope-gating also imposes **MFA**, because every service sets `Auth:ProtectedScopeRequiresMfa=true`. This
  is consistent rather than a regression: any session that can reach `emr:read` is already MFA-backed.
- Services reach masterdata with the **caller's** bearer token (service accounts are forbidden platform-wide),
  so the broad grant covers them.

**Legacy drug rows.** The workbook (22,653) and the earlier CSV (25,099) overlap only partially by normalised
trade name, so after the load the catalogue holds 31,651 drugs of which **8,998 are legacy rows with no
indications**. They are kept — historical prescriptions point at them and reference data is never
hard-deleted — but the phase-26.2 prescribing search should serve the **current** market list
(`source_row_id IS NOT NULL`), so a prescriber is not offered two entries for one product, one of which has
no indication data.
