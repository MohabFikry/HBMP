# 54 — The Check That Checked Nobody

> **Status:** implemented (2026-08-21, extended 2026-08-22).
> **Reads on:** [11](11-permission-matrix.md) §3.2, [13](13-ux-flows.md) §2,
> [32](32-user-stories.md) US-010,
> [17](17-api-specifications.md), [18](18-security-model.md) §2,
> [19](19-audit-strategy.md) §3, [21](21-accessibility-checklist.md).
> **Found by:** reading the eligibility screen after being asked to make it "more legit".

---

## 1. Two calls, and the choice nobody made

The eligibility screen had one text box — *"Card number, national ID, or name"* — and this behind it:

```ts
const hits = await api.searchEligibility(query.trim());
if (hits.length === 0) { /* no match */ }
const res = await api.checkEligibility(hits[0].id, category ?? undefined);
```

`/reception/search` matches names with `ILIKE '%term%'`. So typing **`Ahmed`** returned every Ahmed on the
platform, and `hits[0]` took whichever one the database's ordering put first — a detail of the query plan, not
a decision anyone made. The plan, the benefit band, the **annual cap remaining** and the **visit verdict** then
rendered as that person's answer.

Nothing on the card said a choice had been made. There was no "3 matches" line, no disambiguation step, no
count. A desk reading the result had no way to know the query had been ambiguous at all.

This is a **wrong-patient** defect, not a search-quality one. The two things a desk does with this screen are
admit somebody and turn somebody away, and both were being done on coverage that might belong to a different
member. A cap that reads exhausted turns away a person whose own cap is intact.

### 1.1 The design never said "name"

US-010 is specific, and it was specific from the start:

> *As a Reception officer, I want to search by **ID/Passport/Card/Policy/Phone** so that I can confirm
> eligibility fast.*
> — Given **a valid identifier**, When I search, Then I see a result card…

Doc 13 §2 draws the same flow: *"Reception search: ID / Passport / Card / Policy / Phone"*. Neither document
lists a name, and the acceptance criterion is conditioned on **a valid identifier** rather than on a match.

So this is not a design decision that was later regretted. The screen's single box acquired name matching on
the way to being built — plausibly as a convenience, since `/reception/search` already matched names for the
call centre — and `hits[0]` came with it. The fix restores what US-010 asked for and adds the name **on top**
of the identifier rather than as an alternative to it.

> It is worth being precise about what was and was not broken. `checkEligibility` was fixed in 32.6 and does
> ask the service that owns the rules: the verdict itself is correct, audited, and computed from the plan
> version in force. It is correct **about the beneficiary it was given**. The identification step in front of
> it is what nothing was checking.

---

## 2. What the fix is

`POST /api/v1/reception/verify` — an identifier the beneficiary can present, and part of their name:

| | |
|---|---|
| **Identifier** | Exact match, no wildcard, on member card / national ID / refugee ID / UNHCR number / passport / policy number |
| **Name** | Every term offered must prefix-match a token of the recorded name; two characters minimum |
| **Answer** | The one member both agree on — or a reason code, and nothing else |

The identifier resolves to exactly one member or to none. There is no first-of-several to fall back on,
because there is no several.

### 2.1 A card number is not a member number

They are two identifiers and the platform holds both. `member_no` is the enrolment key policy-service issues
(`MRS-M-…`); `card_number` is what patient-service normalizes and **prints on the object the beneficiary
carries**. The lookup matched every identifier except the second — so a desk typing what was in their hand
found nobody, and the only way forward was to search by name, which is what this whole document exists to
stop.

Nothing new crosses the wire to fix it. `BeneficiaryRegistered` has carried `cardNumber` since the intake
path was written, from **both** of patient-service's registration entry points, and eligibility's
`ProjectionUpdater` read every other field of that event and dropped this one. The same shape as the rest of
this phase: a value published, delivered, and never read.

Migration 0007 adds the column and it is deliberately **not backfilled** — the value lives in another
service, and the projection's whole contract is that it is fed by events. A member whose row predates the
migration is found by member number exactly as before, so nobody becomes unreachable while the projections
catch up. The dev seed writes both, because a development environment where the card in front of you finds
nobody teaches the defect rather than the fix.

Both travel to the screen, and the card is shown beside the member number **only when they differ** —
printing one string twice under two labels teaches a reader that the labels do not mean anything.

### 2.2 Three deliberate exclusions

**A phone number is not an identifier here.** A household shares one, so it names a family and not a person —
which is the single thing this endpoint exists to do. `/reception/search` still matches it, correctly: the
call centre takes a call from whoever is holding the phone, and searching is its job.

**A beneficiary GUID is not either.** It is a system key, not something anyone carries to a desk. Admitting it
would make "verified against what they presented" untrue for that path while the response looked identical.

**A policy number that covers more than one person resolves nobody.** A family policy names a household.
Returning its first member would be the original defect wearing a different identifier.

### 2.3 Why prefix, and why every term

`Contains` would let a two-letter fragment land in the middle of an unrelated name — `li` inside `Khalil` —
which is close enough to matching anything that it would not be a check. **Prefix** is also what a desk
actually does: a name is read off a card or spelled from its beginning.

Every term must match, not any. If `Ahmed Sayed` were satisfied by `Ahmed` alone, then adding the family name
would make a wrong record *easier* to open rather than harder — the operator's extra care working against
them, which is the worst property a check like this can have.

A single letter is refused. One character prefix-matches a large fraction of any name list, so accepting it
would restore the old behaviour at the cost of one keystroke.

---

## 3. The card that showed less coverage than it had

The result card rendered three rows: a plan name, the category list, and one monetary limit. Two of the three
were wrong in different ways.

**The plan name was a hardcoded literal.** The api client sent `"Benefit coverage"` for every member,
because the reception projection carries no plan — so every card printed a plan name that was not a plan
name, and no reader could tell the placeholder from a real one. The field is now nullable and the row is
rendered only when a plan is genuinely known, which today is never. An omitted row is honest; an invented one
is not.

**The limits were collapsed to one number.** The reception card has always carried a
`{category, limitType, remaining}` row *per limit per active coverage*. The client picked the first monetary
one for the headline and discarded the rest. A member with four consultations, EGP 3,200 of laboratory cover
and one imaging study left was summarised as a single annual cap — and the desk could not answer *"how many
consultations do they have?"* from the screen that question belongs on.

Every limit is now rendered, and the **limit type is rendered with it**: `Count` and `Amount` are different
quantities, and the only thing that distinguishes `4` from `EGP 4` is which one the row says. Money is
formatted as money; everything else is shown as the plain number it is, because guessing a unit for a
vocabulary the service owns is how a count of visits becomes a currency figure on a desk.

The policy number travels too — it was on `ReceptionDocument` since 2.2 and was dropped by the card
projection, so the desk was shown a coverage summary that could not say which policy it summarised.

**Not gated on scope.** Naming a benefit category asks an *additional* question — is this service covered,
and what does it cost. It does not make the rest of the coverage less true, and a desk that has to run the
check twice to see the whole picture will run it once.

### 3.1 And the form got shorter

Three fields carried three paragraphs of help text explaining rules the service enforces and reports in its
own words at the moment they apply. The labels carry the instruction now; the two identifier fields sit on
one line, because they are the two halves of a single question rather than two unrelated steps. They stack
below 640px — two half-width boxes on a phone are two fields neither of which fits a fourteen-digit national
ID.

---

## 4. What a refusal may say

The refusal carries a reason code and **no identity**: not the name on file, not the member number, not the
membership status.

An endpoint that answered *"no — that card belongs to Amal Hassan"* would hand the name behind any card number
to whoever is holding one. That is a **larger** disclosure than the ambiguity being removed, and it is the
obvious shape for this endpoint to take if nobody says otherwise. The test asserts on the raw response body
rather than the parsed shape, so a future field that reintroduces the name fails rather than passes.

### 4.1 Not-found and name-mismatch stay distinguishable

They are different situations at the desk and lead to different actions — *re-read the digits* versus *ask
them to say their name again* — and collapsing them would leave an operator unable to tell a typo from the
wrong person.

The cost is real and worth stating: someone holding a card number learns that the card is registered. That is
the smaller disclosure, and it is not left unwatched. The mismatch is audited at **High** severity precisely
because a run of them across different numbers from one desk is somebody trying identifiers, and that pattern
is invisible unless each attempt leaves a row.

Every outcome is audited, including the miss. *"Is this identifier one of yours?"* answered **no** is still an
answer about a person. Where nothing resolved there is no entity to name, so the audit row carries the
**identifier offered** — otherwise the failed attempts would be the only ones an investigator could not
follow.

---

## 5. Where the rule lives

In the service, and this is the part worth defending.

A rule the browser applies is a rule for whoever is looking at that browser. Stated in eligibility-service it
is the same for the SPA, the call centre and anything built next; it is audited once; and it cannot be stepped
over by a caller that skips the check — because on this endpoint there is no path from a name fragment to a
card at all.

The matching rule itself is a **pure function** (`IdentityCorroboration`), because the rule is the whole
feature. Every case worth arguing about — a fragment too short, a match on the family name only, a hyphenated
or prefixed Arabic name, a term the record does not carry — is a question about that method and nothing else.

`Al-Sayed` is one word with two parts, and a desk given `Sayed` is right to expect it to match; tokens split on
hyphens as well as spaces. Casefolding is invariant rather than culture-sensitive, because the Turkish
dotless-i rule would stop an English name matching itself under a `tr-TR` request culture.

---

## 6. Booking: the other list, and the omission in it

`ReceptionBooking` calls the same search, and it was **not** making the same mistake. It has never taken
`found[0]`: one match renders as a row the operator clicks, several open a picker, and a non-bookable member
is refused at the moment they are found rather than at submit. The choice is made by a person, against names
they can see.

What it had was the other failure — not a silent choice but a **silent omission**.

`/reception/search` takes 25 rows and reported the length of that page as `count`. A term matching forty
people produced twenty-five, and nothing in the response distinguished that from a complete answer. The
operator picks from a truncated set presented as the whole of it, and the patient they are looking for may be
among the fifteen that were never sent.

That is the harder one to notice. A silent choice at least shows you *a* patient; a silent omission shows you
a plausible list, and a plausible name in it looks like the right answer.

The search now asks for **one row more than the page** — so "there are more" is a fact rather than an
inference — and returns `truncated`. The screen says so **before** the list, because an operator who has
already found a plausible name will not read a footnote; the reopen control reads `(25+)` rather than `(25)`,
which is a count of what was sent masquerading as a count of what matched; and the audit row records `25+`
for the same reason, since a record claiming exactly 25 preserves the untruth in the one place meant to
settle what happened.

No total is returned. The only thing an operator can do about "too many" is narrow the term, and "more than
25" says that as well as "137" does — at the cost of one query instead of two on every search.

---

## 7. What this is NOT

**Corroboration, not authentication.** It stops the wrong *record* being opened. It does not prove the person
at the desk is the person on the card, and nothing downstream may lean on it as though it did. Somebody
holding another person's card and knowing their name passes this check, exactly as they would pass a paper
one.

Saying so plainly matters more than it looks. A control described as identity verification tends to acquire
weight it was never built to carry — a later decision leans on it, and the gap only surfaces when it fails.

---

## 8. Left undone, deliberately

**`/reception/search` still matches names, phones and partial identifiers.** Booking, the call centre and the
member directory need exactly that, and the operator there chooses from what comes back. What the eligibility
screen needed was never a search. The only change to that endpoint is that it now admits when its page was
cut (§6).

**Two people with the same name are told apart by member number and nothing else.** The reception card carries
no date of birth by design (min-necessary), so a picker showing two `Ahmed Hassan` rows distinguishes them by
a number the operator may not be holding. Adding a corroborating field to that projection is a
minimum-necessary decision, not a code one.

**No date-of-birth check on verify.** A second corroborating field is the natural next tightening, and it is a
policy question — whether a desk may be blocked when a member does not know their recorded DOB — not a code
one.

**The audit row for a miss carries the identifier offered.** That is a deliberate trade (§4), and it means a
mistyped national ID is stored in the audit trail. It is the same class of record `/reception/search` already
writes for its query string.
