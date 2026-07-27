# ADR-0025 — One engine for bulk in and extract out; the rules live with the domain, not with the loader

- **Status:** Accepted
- **Date:** 2026-07-27
- **Phase:** 19.5b

## Context

Design 38 §4.4 requires bulk upload (enrolment, termination, plan change, group assignment, contact update,
provider tier assignment, benefit rule import) and data extract over the same filter vocabulary as the 19.5
queries. The build prompt is explicit that the phase-12.1 migration toolkit and document-service's upload
pipeline are to be REUSED — "do not invent a second importer".

## Decision

### The membership RULES moved out of the HTTP handlers into `MembershipCommands`

The sharpest reading of "do not invent a second importer" is not about the loader's shape — it is about the
rules. A bulk path that re-implements "is the plan in force", "does the member meet the eligibility rule",
"is the beneficiary Active" becomes a way to create memberships the form would refuse, and nobody finds out
until a claim is denied against coverage that should never have existed.

So `services/policy/Infrastructure/MembershipCommands.cs` now owns enrol / terminate / reinstate / change-group
/ change-plan / cancel, and both `EnrollmentEndpoints` and the bulk appliers call it. The HTTP layer keeps what
is genuinely its own: authorization, the `Idempotency-Key` header, and turning a failure into RFC 7807.

`MembershipOptions` moved to the domain with it. A setting only the HTTP layer could read would have meant a
bulk plan change silently using the default carry-forward rule.

### One transaction PER ROW, with an idempotency key derived from (job_id, row_number)

Never one transaction for the file. A 50 000-row job inside a single transaction holds locks for minutes,
blocks every reception desk trying to enrol somebody, and takes the whole file down when row 49 000 fails.

The key contains the job id and the row number and NOTHING else — no timestamp, no GUID, no row content — so
it is stable across a resumed job, a retried commit and a re-run after a crash. `enrollment.idempotency_key`
is uniquely indexed, which is what turns "should not double-apply" into "cannot".

Two layers protect a re-commit and they fail in different circumstances, so both are tested: an Applied row is
not re-processed (row state), and a row whose state was lost between the write and the mark still replays into
a skip (the key). The second is the state a crashed job actually finds itself in.

### Partial failure is the normal outcome, and it is reported

A row that fails is marked `Failed` and the loop CONTINUES; the job ends `Completed` with applied / failed /
skipped counts. Aborting on the first failure leaves the job half-applied with no record of where it stopped —
not atomic, and not accounted for either. `bulk_job` carries a CHECK that submitted = valid + invalid, because
a job that cannot say what happened to a row is one that lost it, and the report still renders.

### An unknown or missing column fails the WHOLE file

The tempting behaviour is to ignore unrecognised columns and get on with it. That is how a file with
`effective_date` where the template says `effective_from` imports ten thousand memberships starting today
instead of in January — every row "valid", nothing in the report. A column the engine cannot place means the
operator and the system disagree about what the file means, and the only safe reading is to stop.

Header matching is case- and separator-insensitive and order-independent, because an operator who capitalises
a header or reorders columns has not made a mistake.

### Spreadsheet dates are read from the underlying value, never from `GetString()`

The single most dangerous conversion in the file. A cell typed as `03/04/2026` and stored by Excel as a date
has no unambiguous string form, and "3 April" and "4 March" are both plausible enrolment dates. CSV parsing
likewise accepts only `yyyy-MM-dd` (and `yyyy/MM/dd`) and REJECTS locale-dependent forms rather than guessing.

### Rollback is a compensating change, per row, and refuses where it would corrupt something

Reversing an enrolment CANCELS it — distinct from terminating it, because a mis-uploaded membership never
should have existed, and a termination would leave the member covered for the days between the upload and the
rollback. Refusal is per ROW: a file of 500 enrolments where three members have since consumed benefit is 497
clean reversals and three that need a human decision, and refusing all 500 leaves the operator with no path.
A job is only marked `RolledBack` when every applied row came back; a partial rollback reporting itself as
complete is the most dangerous state this file could produce.

### Extracts: the column set is intersected with the role, and the withheld columns are NAMED

Silently dropping a column is specifically rejected. A spend report missing `total_consumed` without saying so
is not a narrower report, it is a wrong one. Three outcomes are named — granted, withheld-by-class, unknown —
and `Clinical` is a NAMED, always-denied class rather than an absence, so the refusal reads as a rule that was
applied rather than a column nobody thought of.

The capabilities come from the same role lists 19.5's projections use: a column an officer cannot see on
screen is not one they can extract into a spreadsheet.

### AS-OF means "the facts as now known about that date", not "what we believed then"

Two readings are possible. A member whose termination was later back-dated to 20 February was NOT covered on
1 March under this reading, and does not appear.

This is the reading an as-of extract is nearly always for: reconciling what a payer should have been billed
for a period, restating a report after a correction, answering "who was covered when this happened". The other
question — what the organisation believed on the day — is already answerable, because `enrollment_event`
records `occurred_at` beside `effective_date` and the 19.3c timeline replays it. Conflating the two would give
one number that is wrong for both.

The plan is reconstructed from dated events, not read off the current row. `Enrolled` and `PlanChanged` now
carry `policyPlanId` in their payloads for exactly this; a membership predating 19.5b has no such event, and
its plan is rendered as `"<label> (current; not reconstructed)"` rather than silently shown as March's.

### Operational documents are a separate thing from beneficiary documents

`document.operational_document` (migration 0003) holds bulk uploads, error reports and extracts. Deliberately
not a `document.document` with a null owner: beneficiary documents are listed, authorized and retained BY
OWNER, and a null-owner row is one every owner-scoped query must remember to exclude. A bulk error report also
quotes hundreds of member numbers, so "whose document is this" has no answer the beneficiary model accepts.

They pass through the SAME validate → checksum → fail-closed ClamAV → MinIO pipeline. A second ingest path is
a second way for malware to arrive.

### Download is an authorized, audited STREAM rather than a signed URL

The build prompt asks for "signed, short-TTL, audited". The property that actually matters is that the file
cannot be read by someone never authorized, and cannot be read again after that authorization is withdrawn. A
signed URL is a bearer credential in a query string: it survives in browser history, chat messages and support
tickets, and no revocation reaches it before its TTL expires. Streaming through an authenticated endpoint is a
stronger version of the same guarantee with nothing to leak — and every read writes its own audit event, which
a URL redeemed directly at MinIO never would.

### Scheduled extracts run under an explicit service scope, and an unsupported schedule is refused

`extract_definition.service_scope_payer_ids` is required whenever `schedule_cron` is set — enforced by a CHECK
constraint as well as in the application, because the consequence of getting it wrong is an unattended broad
disclosure on a timer. An empty scope is not "unrestricted"; it is unconfigured, and the schedule will not run.

The schedule grammar is restricted to `@daily` / `@weekly` / `@monthly` / `m h * * *`. An expression this
service cannot evaluate is REFUSED at definition time rather than stored and never fired — a scheduled extract
that silently never runs is discovered by whoever was waiting for the file, months later.

## Consequences

- **A latent ordering bug in the enrolment write path surfaced and was fixed.** `coverage.enrollment_id` has a
  real foreign key in the database (0008) but the EF model never declared it, so an enrolment that adds the
  membership and its generated coverage in one `SaveChanges` had no guaranteed insert order between the two.
  It held one row at a time and broke the moment the bulk engine ran three rows through the same context. The
  relationship is now declared. This affected the single-member endpoint too.
- **`patient-service` gained a contacts write path** (`PUT /beneficiaries/{id}/contacts`) and
  **`provider-service`'s existing tier endpoints are called for tier assignment**. Both with the CALLER's
  token, so each owning service applies its own authorization, validation and audit. A bulk engine writing
  into another service's schema directly would bypass all three, and its author would appear in that service's
  audit trail as nobody at all.
- **Row errors are bilingual.** The people who correct these files work in Arabic; an English-only reason means
  the fix is guessed from a code, and a guessed fix to an enrolment file is somebody's cover.
- The parser ceiling is 200 000 rows and the extract ceiling 100 000 — blast radii rather than performance
  numbers.

## Open

- **The commit loop is synchronous within the request.** The pipeline is built for async execution (job state
  is persisted at every step, rows are independent, the key makes resumption safe) but the commit endpoint
  still runs it inline. A 50 000-row job will hold a request open. Moving it to a hosted worker is a wiring
  change, not a design change, and belongs with the 19.7 operational pass.
- **The scheduled-extract runner is not wired.** Definitions accept and validate a schedule, and the scope
  rule is enforced at write time and in the database; nothing fires them yet.
- `BenefitRuleImport` writes to DRAFT plan versions only, enforced in the applier AND by the 0005 immutability
  triggers. Rolling one back after the version has been activated is refused, correctly — the version is
  immutable and the remedy is an amendment.
