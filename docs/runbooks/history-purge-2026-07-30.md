# History purge — `Raw Files/CPT 2022.PDF` (2026-07-30)

Phase 24 Gate 6. A 61 MB PDF had been in this repository's history since the first commit and was removed
by rewriting every ref. This is the record of what was done, what was verified, and what is still true
afterwards.

## 1. Classification (done first, deliberately)

The gate's own rule is that content is classified **before** it is purged, because if a file contains
beneficiary data then removing it is an incident with notification duties (PDPL Law 151/2020, DPIA
register) and not merely repository hygiene.

The blob is `%PDF-1.4`, created 2021-11-10, a **CPT 2022 procedure code book** — a reference publication.

* **No beneficiary data. No personal data of any kind.** Not a DPIA incident; no notification duty.
* It is AMA-copyrighted licensed content, so its presence in a repository was a **licensing** question
  rather than a privacy one. Worth recording, because the two are easy to conflate and only one of them
  has a clock attached.

## 2. What was done

| Step | Detail |
|---|---|
| Mirror backup | `/home/mohab/hbmp-backup-20260730-224323.git` — 95 MB, all 5 refs, taken before anything else |
| Rehearsal | Full purge run on a throwaway mirror clone first; verified there before touching the real repo |
| Purge | `git-filter-repo v2.38.0 --invert-paths --path "Raw Files/CPT 2022.PDF"` |
| Force-push | `git push --force --all` + `--force --tags`. `refs/replace/*` deliberately NOT pushed |
| Verify | Fresh clone from origin |

`git-filter-repo`, not `filter-branch`: filter-branch is unmaintained, quadratic on large histories, and
leaves the original refs under `refs/original/` where the blob stays reachable — the failure mode where
someone believes a purge happened and it did not.

## 3. Verified after the fact, from a fresh clone

```
clone size          22 MB   (was 96 MB)
CPT 2022.PDF        0 occurrences anywhere in --all
blobs > 5 MB        1, allow-listed with a reason
commits             329     (330 before)
```

The one dropped commit is `chore(raw-files): drop the CPT 2022 code book PDF`. Once the file never
existed, the commit that deleted it has an empty diff and filter-repo prunes it. That is correct, and it
is the only history that changed in content rather than in SHA.

**Every commit SHA changed.** Anything quoting one — an ADR, a status doc, a runbook — now quotes a SHA
that does not exist. `docs/BUILD-STATUS.md` and the phase prompts are the likely holders; a previous purge
(tag `pre-16.1-history-purge`) left the same footprint and needed the same reconciliation.

## 4. What is NOT true yet

* **Branch protection was not re-applied, because there was none.** The GitHub API returns 403
  "Upgrade to GitHub Pro or make this repository public to enable this feature" — protection rules are
  unavailable on this plan for a private repository. Nothing was removed by the force-push and nothing was
  restored after it. If the repo moves to a plan that supports it, protection has to be configured for the
  first time rather than restored.
* **The blob may still be retrievable from GitHub by its SHA until GitHub runs its own GC.** A force-push
  makes objects unreachable; it does not delete them server-side. For a *secret* this would matter and the
  next step would be a support request to run GC plus rotation of whatever leaked. For a copyrighted
  reference PDF the residual risk is low, and it is recorded here rather than assumed away.
* Anyone holding an existing clone still has the old history locally and must re-clone (or
  `git fetch --all && git reset --hard origin/<branch>`). A stale clone that pushes would reintroduce it.

## 5. What stops a recurrence

`tools/ci/check-large-blobs.py` inspects the blobs a commit **adds** and fails above 5 MB unless the path
carries a written reason:

* `.githooks/pre-commit` — before it is ever committed (`git config core.hooksPath .githooks`)
* `backend-ci` gate `large-blobs` — `--range` over what the push adds, falling back to a whole-history
  audit when there is no usable base (a first push, or a force-push like this one)

One allow-list entry: `Raw Files/Egyptian Drugs - ATC Classified.csv` (6.3 MB), the drug master the ATC
loader reads — text, diff-able, and the source of a table the platform cannot be built without.

The purge was the easy half. The guard is the half that decides whether this document gets written again.
