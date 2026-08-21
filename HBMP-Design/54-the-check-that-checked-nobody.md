# 54 — The Check That Checked Nobody

> **Status:** implemented (2026-08-21).
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

### 2.1 Three deliberate exclusions

**A phone number is not an identifier here.** A household shares one, so it names a family and not a person —
which is the single thing this endpoint exists to do. `/reception/search` still matches it, correctly: the
call centre takes a call from whoever is holding the phone, and searching is its job.

**A beneficiary GUID is not either.** It is a system key, not something anyone carries to a desk. Admitting it
would make "verified against what they presented" untrue for that path while the response looked identical.

**A policy number that covers more than one person resolves nobody.** A family policy names a household.
Returning its first member would be the original defect wearing a different identifier.

### 2.2 Why prefix, and why every term

`Contains` would let a two-letter fragment land in the middle of an unrelated name — `li` inside `Khalil` —
which is close enough to matching anything that it would not be a check. **Prefix** is also what a desk
actually does: a name is read off a card or spelled from its beginning.

Every term must match, not any. If `Ahmed Sayed` were satisfied by `Ahmed` alone, then adding the family name
would make a wrong record *easier* to open rather than harder — the operator's extra care working against
them, which is the worst property a check like this can have.

A single letter is refused. One character prefix-matches a large fraction of any name list, so accepting it
would restore the old behaviour at the cost of one keystroke.

---

## 3. What a refusal may say

The refusal carries a reason code and **no identity**: not the name on file, not the member number, not the
membership status.

An endpoint that answered *"no — that card belongs to Amal Hassan"* would hand the name behind any card number
to whoever is holding one. That is a **larger** disclosure than the ambiguity being removed, and it is the
obvious shape for this endpoint to take if nobody says otherwise. The test asserts on the raw response body
rather than the parsed shape, so a future field that reintroduces the name fails rather than passes.

### 3.1 Not-found and name-mismatch stay distinguishable

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

## 4. Where the rule lives

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

## 5. What this is NOT

**Corroboration, not authentication.** It stops the wrong *record* being opened. It does not prove the person
at the desk is the person on the card, and nothing downstream may lean on it as though it did. Somebody
holding another person's card and knowing their name passes this check, exactly as they would pass a paper
one.

Saying so plainly matters more than it looks. A control described as identity verification tends to acquire
weight it was never built to carry — a later decision leans on it, and the gap only surfaces when it fails.

---

## 6. Left undone, deliberately

**`/reception/search` is unchanged.** Booking, the call centre and the member directory search by name, phone
and partial identifier, and that is what those surfaces are for. What the eligibility screen needed was never
a search.

**Booking still takes `found[0]`.** `ReceptionBooking` calls `searchEligibility` and uses the first hit the
same way this screen did. It is the same shape of defect on a screen that then shows the member's name before
anything is committed, so it is visible in a way the eligibility card was not — but it is not fixed here, and
it should be.

**No date-of-birth check.** A second corroborating field is the natural next tightening and it is a policy
question — whether a desk may be blocked when a member does not know their recorded DOB — not a code one.

**The audit row for a miss carries the identifier offered.** That is a deliberate trade (§3), and it means a
mistyped national ID is stored in the audit trail. It is the same class of record `/reception/search` already
writes for its query string.
