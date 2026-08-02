# `tools/dev` — local environment data

Scripts that build a working dev environment. **Dev only.** Every person, patient and clinical note in here is
invented; the identifiers are format-valid so the services accept them and search finds them, and that is the
only thing they have in common with a real record (CLAUDE.md: never real PHI in lower environments).

Run against the Compose Postgres:

```bash
cd infra/compose && docker compose up -d postgres
psql -h localhost -p 55432 -U hbmp -d hbmp -v ON_ERROR_STOP=1 -f tools/dev/<script>.sql
```

## Order

| # | Script | What it does |
|---|--------|--------------|
| 1 | `reset-dev-data.sql` | Truncates business data across 18 schemas. **Keeps** identity, master data, admin and audit. Refuses to run outside the `hbmp` database. |
| 2 | `restore-reference-structure.sql` | The organisation: 6 branches (ASW, ALX, OCT, MAA, DOK, NSR), 3 providers, network tiers, 6 practitioners, clinic rosters. Transcribed from the environment as it stood — **not invented**. |
| 3 | `seed-dev-clinic.sql` | 25 beneficiaries with policies, coverage, eligibility projections, a fortnight of slots, appointments and call history. |
| 4 | `seed-branch-management.sql` | Makes every branch-management alert path demonstrable: an expiring licence, an expired one, a roster exception, low stock, and expired batches in quarantine. |
| 5 | `seed-doctor-account.sql` | Makes the `doctor` login a real practitioner — specialty, two branches, roster, and a day's clinic in mixed states. |

Steps 2–5 are idempotent; step 1 is not, and it is the destructive one.

**Structure is not test payload.** Reset the business data, keep the organisation. An earlier pass regenerated
the branches with invented codes and uuids, which broke every screenshot, saved URL and frontend fixture that
referred to them. That is why step 2 exists as a separate file.

## The convention that is easy to get wrong

**`provider.practitioner.practitioner_id` must equal the doctor's `identity."user".id`.**

Nothing enforces it, and three separate rules depend on it. All of them resolve the doctor from the **subject
of the access token**, never from a client-supplied id — which is the correct security choice, and also the
reason a mismatch fails silently:

| Where | Rule |
|---|---|
| `GET /appointments?mine=true` | `a.DoctorId == Guid.Parse(me.Principal.Subject)` — the doctor's own day list |
| `POST /encounters` | `VisitStartRules.MayStart(appt, callerId)` — 403 `not-the-assigned-doctor` |
| `GET /encounters/mine` | `e.CreatedBy == p.Subject` — the "My Patients" panel |

Meanwhile the booking screen writes `appointment.doctor_id` from `PractitionerView.PractitionerId`
(`apps/web/src/screens/booking/bookableDoctors.ts`, `p.id`). So the id a booking records and the id a token
carries have to be the same value.

Get it wrong and nothing errors: the practitioner appears in every picker, appointments book against them
happily, and their portal is simply empty — "My Visits" shows nothing and "Start visit" answers 403. The six
practitioners from step 2 are in exactly that state. Their `user_id` column holds slugs (`seed-dr-hala`,
`demo-dr-hana`) matching no account at all. They are fine as bookable names; none of them is a person who can
sign in. Step 5 is the one doctor wired end to end.

If you add another signed-in clinician, derive the practitioner id from the account rather than inventing one —
and read it from `identity."user"` rather than hardcoding, because `UserSeeder` mints those ids with
`Guid.NewGuid()` on first startup and they differ per environment.

## `dev-token.sh` — an access token from the command line

```bash
tools/dev/dev-token.sh doctor
TOKEN=$(tools/dev/dev-token.sh reception)
curl -H "Authorization: Bearer $TOKEN" \
     -H "X-Active-Branch: 0190b100-0000-7000-8000-000000000005" \
     "http://localhost:8000/api/v1/appointments?mine=true"
```

Takes any demo role name (username = role). The issuer allows only authorization_code + PKCE for the SPA
client — no password grant, deliberately — so the script drives the same flow a browser does: sign in at
`/connect/login` with the antiforgery token, then redeem the code. It needs the demo password from
`infra/compose/.env` and works only against the dev issuer.

It requests the scope set from the **registered client row**, not from `apps/web/src/config.ts`. A narrower
token authenticates fine and then 403s on the first endpoint guarding a scope that was left out, which reads
like a permissions bug and is not one. The token still only carries what the caller's role grants — asking is
not receiving, so the `doctor` token comes back with 23 of the ~90 requested.

`X-Active-Branch` matters for branch-scoped roles. Without it the caller falls back to their Home branch.
