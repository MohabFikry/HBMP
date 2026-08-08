# ADR-0036 — Signing in without leaving the application, and resetting a forgotten password

- **Status:** Proposed
- **Date:** 2026-08-05
- **Phase:** 28
- **Amends:** ADR-0015 (in-app identity on OpenIddict) — the issuer stays exactly where it is; only the
  *interactive surface* moves.
- **Related:** `docs/security/token-contract.md`, `18-security-model.md`, `19-audit-strategy.md`,
  `21-accessibility-checklist.md`, ADR-0021 (user access model — memberships)

---

## 1. What was asked, and what it actually asks for

> "The app should no longer move to another platform to log in — log in from the same page, with the ability
> to reset a password as well."

Two requests, and they are not the same size.

The first is a **surface** change: the browser must stop navigating to something that looks like a different
system. The issuer, the token contract, the scopes, the membership model and the second factor are all
correct and stay exactly as they are. What is wrong is that the person meets them through a page that does not
look like, sound like, or behave like the rest of the platform.

The second is a **capability that does not exist**. There is no self-service password reset in this codebase at
all. What exists is an administrator typing a new password into `POST /identity/admin/users/{id}/reset-password`
on somebody's behalf — which is a different thing with a different security story, and one this ADR ends.

---

## 2. Context: what is true today

Verified against the tree, not remembered.

### 2.1 The login is a second application, and it looks like one

`services/identity/Api/Auth/AccountPages.cs` is 349 lines of C# string-literal HTML: a password page, a TOTP
challenge, an authenticator-enrolment page, a recovery-code page and a membership chooser. It has its own
inline `<style>` block (`max-width:26rem`, `system-ui`, a hand-written focus ring), its own English/Arabic
dictionary, and its own accessibility. It is genuinely careful work — 44px targets, `role="alert"`, RTL — and
it is a **complete parallel implementation** of things the design system already owns.

Nothing keeps the two in step. When the Mersal palette, the focus treatment, the Arabic type stack or the
error convention changes in `@mersal/design-system`, this file does not change with it, and no gate notices.

### 2.2 The SPA's live login is a single button

`apps/web/src/pages/LoginPage.tsx` under `LIVE` renders one button whose only job is `window.location.assign`
to `/connect/authorize`. Its own header comment already records the smell:

> Keycloak was retired then; this screen still named it in the user-facing copy long afterwards, **which told
> every operator that their credentials went somewhere they no longer do.**

That was a copy bug. The structural version of it is still here: the screen's entire purpose is to send you
somewhere else.

### 2.3 The gateway route already exists; the dev configuration does not use it

`infra/compose/config/kong.yml` routes `/connect` and `/.well-known` to identity-service — the comment there
says it explicitly, *"so the SPA reaches one origin"*. But `apps/web/src/config.ts` defaults
`VITE_OIDC_AUTHORITY` to `http://localhost:8090` while the app is served from `http://localhost:5173`. So the
one-origin intent was designed and then not wired, and the URL bar changing host is exactly what the user is
reporting.

### 2.4 Signing in is not one step — it is up to four

This is the fact that decides the whole design. `POST /connect/login` can end in:

1. signed in (`amr=pwd`);
2. `RequiresTwoFactor` → `/connect/2fa` (TOTP **or** a recovery code);
3. signed in but with no second factor enrolled — protected scopes stay denied downstream by `MfaEvaluator`
   until `/connect/enroll-2fa`;
4. signed in, but the identity holds **more than one active membership** (ADR-0021 / 21.1c), so
   `/connect/authorize` bounces to `/connect/select-membership` before it will mint anything.

Plus lockout, plus a deactivated account.

Any design that models login as "exchange a password for a token" cannot express steps 2, 3 or 4.

### 2.5 The cookies are already hardened for a same-site flow

`IssuerSetup.cs` sets `SameSite=Strict` on all four Identity cookies, and its comment states the assumption:

> nothing in this flow is a legitimate cross-site navigation

That is true today and must stay true. A login form posted from a *different* origin is, by definition, a
cross-site request, and `Strict` would drop the session cookie on the response. **The hardening that is
already in place only works if the SPA and the issuer share an origin.**

### 2.6 The issuer URL is pinned, so moving the calls is cheap

`o.SetIssuer(config["Issuer:PublicUrl"])` — added after tokens minted at `:8090` were rejected when the same
request arrived through Kong at `:8000` (ID2088). Because `iss` is pinned rather than derived from the
request, the SPA can call `/connect/token` through the gateway origin and the claim is unchanged. **No change
is required to the 19 services that pin `Auth__ValidIssuers`.** This is worth stating because the opposite
would have made §3 an expensive migration instead of a cheap one.

### 2.7 Login history and the concurrent-session cap live in the login path

`SessionService.RecordAttemptAsync` is called on every outcome — success, bad credentials, lockout, inactive,
failed second factor — and feeds `/identity/me/login-history` (21.5). `SessionService.OpenAsync` applies the
concurrent-session cap and revokes the oldest. Both are called *inside* the HTML handlers.

A new login path that forgets them does not fail loudly. It silently empties the screen that exists to show a
user their account is being attacked, and silently stops enforcing a cap. Recorded here because that is a
defect with no symptom.

### 2.8 There is no password reset, and no way to deliver one

- Self-service: **nothing**. No forgot-password endpoint, no reset page, no email template.
- Administrative: `POST /identity/admin/users/{id}/reset-password` takes `{ "newPassword": "..." }` — so the
  administrator **chooses and therefore knows** the user's password.
- Delivery: `LoggingEmailProvider` is the registered `IEmailProvider`. It writes a log line. There is no SMTP
  client, no relay configuration and no mail container in Tier 1. `SmsChannel` and `WhatsAppChannel` log
  "not yet enabled" and perform no send.

So a reset flow cannot be built as designed-and-mostly-working: **its delivery leg does not exist**, and that
constrains §6 rather than being a detail of it.

### 2.9 There is no Content-Security-Policy anywhere in the repository

Searched: no `Content-Security-Policy` header is set by Kong, by any service, or by the web image. This is
already a gap. It becomes a *sharper* gap in §3 and is treated as a blocking prerequisite rather than a nice
follow-up — see §8.1.

---

## 3. Decision 1 — A first-party login API, then a silent authorize

The SPA renders the login, the TOTP challenge, the enrolment, the membership choice and the reset screens in
React, using the design system, i18n and the a11y gate every other screen answers to.

Those screens do **not** obtain tokens. They drive a small set of first-party endpoints on identity-service
that establish the ordinary Identity session cookie — the very same cookie `POST /connect/login` sets today —
and then the SPA runs the **existing** authorization-code + PKCE flow with `prompt=none`. Authorize finds the
cookie, mints the code without any interaction, and everything downstream is untouched.

```
  SPA  ── POST /connect/session          (username + password)
       ←─ { status: "two_factor_required" }
  SPA  ── POST /connect/session/2fa      (TOTP or recovery code)
       ←─ { status: "authenticated" }          ← cookie now stamped amr=[pwd,otp]
  SPA  ── GET  /connect/authorize?prompt=none&…PKCE…
       ←─ 302 back to the SPA with ?code=…
  SPA  ── POST /connect/token            (unchanged)
       ←─ access + refresh token         (unchanged contract, unchanged iss)
```

**What this preserves, by not touching it:** the frozen token contract; `TokenPrincipalFactory`; the
membership re-resolution on every authorize and every refresh; scope narrowing; `MfaEvaluator`; refresh-token
rotation; the single-flight renew and the stale-scope guard in `oidcClient.ts`; `roleFromClaimRoles` failing
closed to "no portal assigned".

### 3.1 Why not the resource-owner password grant

Posting the username and password straight to `/connect/token` is one endpoint and a day's work. It is wrong
here for a reason more specific than "OAuth 2.1 removes it":

**The token endpoint has no way to say "now give me your TOTP code."** Its response is a token or an error.
Every one of the four outcomes in §2.4 would have to be crammed into an OAuth error code and re-derived by the
client, or — far more likely, because it is the path of least resistance — the second factor and the
membership choice would quietly stop being part of signing in. That is not a login redesign. It is removing
MFA from a platform whose admin scopes and break-glass are gated on it.

It also requires `AllowPasswordFlow()` on the issuer, which is a grant type that then exists for everyone
forever, including for anything that finds it later.

### 3.2 Why not simply restyle the server-rendered pages

Cheapest option: put `/connect` behind Kong so the origin matches, and restyle `AccountPages.cs` to look like
Mersal. It fixes the URL bar and about half the complaint.

Rejected because it makes the §2.1 problem permanent and worse: the platform would then have two
*deliberately identical-looking* login implementations, one in React and one in C# string literals, that must
be kept in visual and behavioural agreement by hand, with no test able to compare them. And it still leaves a
full page navigation, no client-side validation, and a form that cannot participate in the SPA's error,
loading or announcement conventions.

### 3.3 Full-page navigation for the silent authorize, not a hidden iframe

The `prompt=none` round trip is done with `window.location.assign`, exactly as the login redirect is done
today — not in a hidden iframe.

An iframe would preserve SPA state across the round trip, which is worth nothing here: the only screen that
initiates it is the login screen, and there is no state to lose. In exchange it would introduce a framing
dependency (`X-Frame-Options` / `frame-ancestors`) into the authentication path, where a future
clickjacking-hardening change would break login and the failure would look like an authentication bug.

Silent *renewal* mid-session already uses the refresh token (18.C1) and needs no frame either.

### 3.4 `prompt=none` must be honoured, and currently is not

`ConnectEndpoints.cs` responds to an unauthenticated authorize with `Results.Challenge(...)`, which redirects
to `/connect/login`. Under `prompt=none` that is wrong per OIDC Core §3.1.2.1 and wrong for us: the SPA would
be bounced to the HTML page it exists to replace.

The authorize handler gains a prompt check and returns the specified errors instead:

| Situation under `prompt=none` | Response |
|---|---|
| No session cookie | `error=login_required` |
| Session exists, membership cannot be resolved | `error=interaction_required` |
| Session exists, no grantable scope | `error=invalid_scope` (unchanged) |

**This is also the loop-breaker.** The SPA never renders the server's login page, so an authorize that cannot
proceed must terminate in an error the SPA can read, not in a redirect to a page it will not follow. The
protocol already specifies exactly this; we are simply not implementing it yet.

---

## 4. Decision 2 — One origin is a hard requirement, not a deployment preference

The SPA and `/connect/*` must be served from the same origin in every environment.

Three independent things depend on it, and they fail in three different ways:

1. **The session cookie.** `SameSite=Strict` (§2.5) drops the cookie on a cross-site POST. The login would
   appear to succeed and the subsequent authorize would report `login_required` — a symptom that looks like
   a credential problem and is a browser policy.
2. **The URL bar.** A same-origin `prompt=none` navigation never shows another host. This is the user's
   actual complaint, and the redirect is only invisible if the origin matches.
3. **Third-party cookie deprecation.** A cross-site variant would be one browser release away from breaking
   entirely, with no code change on our side.

Concretely: `VITE_OIDC_AUTHORITY` becomes the gateway origin, and the Vite dev server proxies `/connect`,
`/.well-known` and `/identity` to Kong so `5173` is one origin during development. `Issuer:PublicUrl` stays as
it is (§2.6) — the `iss` claim does not move, and no validating service is touched.

**A gate enforces it.** A build-time check fails when the configured authority's origin differs from the app's
own. This is exactly the class of misconfiguration that produces a working development environment and a
broken production one, and §2.3 is evidence that "we intended one origin" does not survive on its own.

---

## 5. Decision 3 — The endpoints return a *status*, never a token

`POST /connect/session` and `POST /connect/session/2fa` return one of a closed set of statuses. They never
return a token, an id, a role, a display name, or anything else about the account.

| Status | Meaning | What the SPA shows |
|---|---|---|
| `authenticated` | Cookie stamped; membership resolved or unambiguous | proceed to silent authorize |
| `two_factor_required` | Password accepted, second factor outstanding | TOTP screen, recovery-code link |
| `membership_selection_required` | More than one selectable membership | chooser, options in the payload |
| `no_membership` | Authenticated, and may act nowhere | "your account has no active organization" |
| `locked` | Account temporarily locked, with `retryAfterSeconds` | "locked, try again in N minutes" |
| `invalid_credentials` | Everything else | one generic message |

> **Amended during 28.3.** `no_membership` is a sixth status, added while building the five above. The state is
> real — `/connect/authorize` already refuses it with `access_denied` — and reachable only with correct
> credentials, so it leaks nothing an attacker could use. Folding it into `invalid_credentials` would tell
> somebody whose password was exactly right that it was wrong, sending them to reset a password that was never
> the problem: the same mistake §5.2 refuses to make for lockout, arriving from another direction.

Every reply also carries a **fresh antiforgery request token** (`csrf`). Not a convenience: ASP.NET Core binds
an antiforgery token to the authenticated user, so the token fetched while anonymous stops validating the
instant the password step signs somebody in, and the next call in the sequence is refused with a 400 that
looks like a client bug. The alternative — a documented rule that the client must re-fetch after each step —
is invisible when broken, because it only bites on sequences with more than one step, which are exactly the
sequences that have a second factor. Handing the next token back with each reply cannot be forgotten.

### 5.1 What each failure is allowed to reveal

| Situation | Status returned | Audit reason recorded |
|---|---|---|
| No such username | `invalid_credentials` | `BadCredentials` |
| Wrong password | `invalid_credentials` | `BadCredentials` |
| Account deactivated | `invalid_credentials` | `Inactive` |
| Wrong TOTP / recovery code | `invalid_credentials` | `TwoFactorFailed` |
| Locked out | `locked` | `LockedOut` |

The internal distinction survives in the audit record and dies at the API boundary — which is the discipline
`ConnectEndpoints.cs` already states for the no-such-user and wrong-password pair:

> Both … record the SAME coarse reason, so the distinction cannot leak into a support screen and become a
> user-enumeration oracle.

### 5.2 Lockout is told, and that is a trade-off taken on purpose

`locked` is a distinct status, so it does leak that a username exists. Accepted, for two reasons.

The codebase already argues the first, in `AccountPages.cs`:

> not a wrong password, and saying "invalid credentials" **sends someone to reset a password that was never
> wrong**

With a reset flow now existing (§6), that consequence gets materially worse: a locked-out nurse is told her
password is wrong, resets a password that was correct, and the reset does not unlock her account. She has now
lost her password *and* is still locked out.

The second: an attacker who is willing to spend five failed attempts per username can already distinguish
accounts by *behaviour* — a real account starts refusing a known-good password, a fictional one never changes.
Reporting the lock costs a bounded amount of information that persistence obtains anyway, and it is an attack
that locks real staff out of a clinic, i.e. one we want to be loud rather than subtle.

### 5.3 Everything the HTML path records, these endpoints record

`RecordAttemptAsync` on every outcome and `OpenAsync` on every success, per §2.7 — asserted by tests that
compare the two paths' behaviour rather than each path against itself. A login history that only shows the
sign-ins that happened through the *old* form is worse than no login history, because it reads as complete.

### 5.4 CSRF

These endpoints are cookie-authenticated and therefore CSRF-relevant, exactly as the forms are. `SameSite=Strict`
is the primary defence and antiforgery remains the secondary one: the SPA fetches a token from a
`GET /connect/session/antiforgery` and sends it as a header. The reasoning in `AccountPages.AntiforgeryField`
about `/connect/enroll-2fa` — where CSRF is *account takeover*, because a forged enrolment makes the
attacker's authenticator the victim's second factor — applies unchanged to its API equivalent, and is the
reason enrolment is in scope here rather than deferred.

---

## 6. Decision 4 — Self-service password reset

```
POST /connect/forgot-password   { username, lang }   → 202 always   (or 503, see §6.3)
POST /connect/reset-password    { userId, token, newPassword }
```

### 6.1 The token is stateless, single-use for free, and short-lived on purpose

ASP.NET Identity's `GeneratePasswordResetTokenAsync` is a data-protector token bound to the user's
**security stamp**. `ResetPasswordAsync` rotates that stamp, so every previously issued reset token — and every
outstanding one, if two were requested — stops verifying at the instant the password changes. Single-use
without a table, without a sweeper, and without a race between two tabs.

**No migration is required.** That is a real saving and it is also the reason not to invent a bespoke token
table: a hand-rolled one would need its own expiry, its own single-use enforcement and its own cleanup, and
would get one of the three wrong.

It is registered as a **named** token provider with its own lifespan (30 minutes) rather than by lowering
`DataProtectionTokenProviderOptions.TokenLifespan`, which is global — shortening reset must not silently
shorten email confirmation and every other data-protection token to 30 minutes as a side effect.

### 6.2 What a successful reset does, and what it must not do

**Does:** rotates the security stamp; calls `SessionService.RevokeAllAsync`, killing every session and every
refresh token for that account. If the reset was requested *because* the account was compromised, leaving the
attacker's live session running defeats the entire exercise. The user signs in fresh afterwards; the reset
endpoint does not sign anybody in.

**Must not:** touch the second factor. A reset does not disable 2FA, does not clear the authenticator key, and
does not consume a recovery code. If it did, control of a mailbox would become a complete account takeover
primitive and MFA would be decorative on exactly the accounts worth attacking. A user with 2FA who resets
their password still meets `two_factor_required` on the next sign-in — **this is correct, and the screen says
so before they start**, so nobody resets a password expecting it to solve a lost-phone problem.

A lost authenticator is answered by a recovery code, and after that by an administrator. It is not answered
here.

### 6.3 It refuses to lie about delivery

`forgot-password` returns `202` whether or not the username exists — the standard non-enumeration answer.

But there is a second, sharper failure available: §2.8 shows the only registered `IEmailProvider` **writes a
log line and returns success**. Left alone, the screen would say *"if that account exists, we have sent you a
link"* while nothing was sent, forever, with no error anywhere. That is the platform's own forbidden pattern —
a failed operation rendered as a clean result — applied to the one screen a locked-out user reaches when
nothing else works.

So: when no real delivery provider is configured, `forgot-password` returns **503** and the SPA does not offer
the link at all. A capability that cannot work is absent, not broken and pretending.

And because a reset that cannot be delivered is not a reset, **a real SMTP transport is in scope**, not a
follow-up: MailKit in `libs/email`, with Mailpit in Tier 1 so the flow is demonstrable end to end locally, and
relay configuration for deployed tiers.

> **Corrected during 28.5.** This section originally said the new transport would go *behind the existing
> `IEmailProvider` seam* and would "also fix every other notification email". It does not, and the reason is
> worth recording rather than quietly dropping.
>
> `IEmailProvider.SendAsync` takes a **recipient user id**, not an address — the logging stub never needed
> anywhere to send to. notification-service's `Notification` entity stores no email address and the service
> has no directory lookup, so an SMTP client wired in there would be a client with nowhere to send.
>
> So `libs/email`'s `IEmailSender` takes an **address**, and identity-service is its first caller because it
> is the service that holds one. Every other notification email still goes nowhere, and closing that needs a
> recipient-address resolution notification-service does not have. It is a real gap, now stated as one
> instead of assumed closed.

### 6.4 Rate limits

`forgot-password` and `reset-password` both join the existing `IssuerRateLimits.Credential` policy — 10/min
per client IP, shed rather than queued.

**Residual risk, stated rather than solved:** that partition is by IP, so it does not bound how many reset
emails a *single account* can be sent from many sources. The blast radius is a mailbox filling up, not a
compromise — every one of those tokens still needs the mailbox to be useful. A per-account cap needs state
this ADR is not adding; it is written down as a follow-up rather than implied to be handled.

### 6.5 The administrative reset changes shape

`POST /identity/admin/users/{id}/reset-password` currently takes a `newPassword`, which means an administrator
knows a user's password and there is no moment at which only the user does. It is replaced by an endpoint that
issues a reset link to the user, audited as an administrative action. The user then sets a password nobody
else has seen.

This is a genuine scope addition rather than a refactor, and it is here because it is the *same decision*:
either a password is a secret only its owner knows, or it is not. Shipping self-service reset while leaving a
back door that hands an administrator a working credential would answer that question both ways.

---

## 7. Decision 5 — The server-rendered pages stay, and are frozen

`AccountPages.cs` and the `GET`/`POST` form handlers on `/connect/login`, `/connect/2fa` and
`/connect/select-membership` are **not deleted**. `/connect/authorize` without `prompt=none` — any future
non-SPA client, any deep link arriving cold, any diagnostic — must still terminate in something a human can
use, and OIDC requires an interactive login to exist.

They are, from this point, **frozen**: bug fixes and security fixes only, no new capability. Every interactive
step added from here is added to the SPA. The alternative — two evolving login surfaces — is precisely the
duplication §2.1 identifies as the problem, and re-creating it while claiming to fix it would be the worst
outcome available.

They are also no longer on any path the SPA can reach, because the SPA always sends `prompt=none` (§3.4).

---

## 7A. Amendment (28.11) — `GET /connect/entitlement`, and the guard it replaces

**Added after the fact, because the first attempt at this was a defect.**

### 7A.1 What went wrong

A client-side guard, `tokenHasCurrentScopes`, required the access token to carry **every scope in the SPA's
request list**. The issuer grants the *intersection* of the request with the user's role entitlement — the
behaviour `TokenPrincipalFactory.GrantableScopes` exists to enforce, and which `config.ts` describes as
"asking is not receiving". Measured against the running issuer:

| role | scopes granted | scopes requested |
|---|---|---|
| `reception` | 15 | 80 |
| `doctor` | 22 | 80 |
| `super_admin` | 21 | 80 |

No role in the system could satisfy it. `restore()` therefore cleared the tokens on **every page load**, and
the same call in `renew()` wiped a healthy session sixty seconds before each token expired — so a user was
signed out on every refresh, and roughly every five minutes without one. Its four unit tests passed because
each of them fabricated a token carrying the whole request list, a token no issuer here can mint.

### 7A.2 The two halves of scope staleness

The underlying problem is real: token scopes are frozen at authorisation, and the refresh grant (§`ConnectEndpoints`,
refresh branch) re-mints from the *current* entitlement but constrains the result to the scopes on the stored
grant, because a refresh must never widen authority. So a session can outlive a scope it ought to have.

There are two distinct causes, and only one of them is visible from the browser:

1. **The app's request list moved** — a release adds a scope to `OIDC.scope`. Detectable locally: the request
   string is recorded beside the token, and `scopeRequestChanged` compares it. No round trip.
2. **The user's entitlement moved** — an administrator adds a scope to a role. **Not** detectable locally.
   The gap between what a token asked for and what it carries is normally just least privilege working, and
   nothing in the browser can tell the two apart.

### 7A.3 The endpoint

`GET /connect/entitlement` answers the second: the platform scopes this caller would be granted on a fresh
authorisation, resolved exactly as the refresh grant resolves them — from the **membership**, never the
identity-level roles, or the client would chase scopes no token for this session could carry.

- **Bearer-authenticated**, own data only. Subject and membership come from the token, so there is no
  parameter with which to ask about anyone else; minimum-necessary holds by construction.
- **Not a revocation channel.** A suspended membership answers 403, but the control that *ends* such a session
  is the refresh grant, within the access token's 5-minute lifetime. Nothing about authority enforcement
  depends on this endpoint being called.
- **Not audited.** No PHI, no other person's data, once per page load. An audit row per reload would bury the
  disclosure events the trail exists to make findable.

The client intersects the answer with its own request list — the policy decision stays with the client,
because only the client knows which scopes it intends to use — and re-authorises when something it needs is
missing.

### 7A.4 The loop guard is the load-bearing part

The remedy is a full-page navigation to `/connect/authorize`. A re-authorisation that came back still short of
the scope — a role changed again mid-flight, a `config.ts` naming a scope the issuer has never heard of —
would bounce the browser between app and issuer with no screen in between, and the user could not reach a
login form to escape it.

So: **one attempt per problem**, recorded in `sessionStorage` before navigating and cleared only when a later
load finds nothing missing. The second failure keeps the narrow token and lets the 403 happen. A session short
one scope is a bad afternoon; an infinite redirect is an unusable portal.

Everything about the check fails **open** — unreadable token, unreachable issuer, unexpected body, no
`sessionStorage` — because it runs in the bootstrap path, and a check that could strand the portal would be a
worse defect than the one it finds.

---

## 8. What must not change, and what we are accepting

### 8.1 A Content-Security-Policy is a prerequisite, not a follow-up

Today an XSS in the SPA can steal a token from the token store. After this change it can additionally
**keylog the password**, because the password is now typed into the SPA.

This is the one real security regression in the design and it must not be waved away. Note what it is *not*:
the redirect model does not protect the password from a compromised front end either — an attacker with script
execution can render a convincing login form and collect the password anyway. What actually changes is that
the legitimate flow now has a password field in the SPA's DOM, so the attack needs no forgery and leaves no
visual tell.

The mitigation is the one that is already missing (§2.9): a real CSP on the web origin, no inline script,
frame-ancestors denied. It ships **before** the SPA login is enabled, not after.

### 8.2 Unchanged, and asserted to be unchanged

The token contract; `iss`; the 5-minute access token and 10-hour refresh; PKCE; refresh rotation and the
reuse-detection behaviour; membership re-resolution on authorize and refresh; scope narrowing;
`MfaEvaluator`'s gate on protected scopes; the stale-scope guard; the 30-minute idle window; cookie hardening.

### 8.3 The dev (no-backend) build is untouched

`DevAuthClient` and the role picker under `LIVE=0` stay exactly as they are. They are how the frontend suite
runs without an issuer, and 874 tests depend on it.

### 8.4 Accepted trade-offs, collected

| Accepted | Because |
|---|---|
| Lockout is distinguishable (§5.2) | The alternative sends a locked-out user to reset a correct password |
| Two login surfaces exist (§7) | OIDC needs an interactive login; one of them is frozen |
| No per-account reset cap (§6.4) | Needs state; blast radius is a full mailbox, not a compromise |
| Password enters the SPA's DOM (§8.1) | Mitigated by CSP, which was missing anyway |

---

## 9. Build order

Each step is independently shippable and independently useful; none of them leaves the platform unable to log
in, because the existing flow keeps working until the last step switches the SPA over.

| # | Step | Ships alone? |
|---|---|---|
| **28.1** | **The credential rate limit partitions on the gateway, not the client (§9.1).** A prerequisite, found while surveying for the step below. | Yes — fixes a live vulnerability on its own |
| 28.2 | One origin + CSP. nginx/Vite proxy, `VITE_OIDC_AUTHORITY` to the gateway origin, the origin gate, the CSP header. The existing redirect login now happens without the URL bar changing host. | Yes — a visible improvement with no new surface |
| 28.3 | `prompt=none` handling on `/connect/authorize` (§3.4) + the session status endpoints (§5) with audit and session-cap parity (§5.3), behind no UI | Yes — server-only, tested by contract |
| 28.4 | The SPA login: credentials → TOTP → membership → enrolment, design system, EN/AR, axe | Yes — this is the user's request |
| 28.5 | Delivery: MailKit provider, Mailpit in Tier 1, the 503-when-unconfigured refusal (§6.3) | Yes — also fixes every other notification email |
| 28.6 | Password reset: forgot/reset endpoints, named short-lived token provider, revoke-all, the SPA screens | Yes |
| 28.7 | The administrative reset becomes link-issuing (§6.5); freeze notice on `AccountPages.cs` | Yes |

### 9.1 Why a step was inserted at the front

Surveying for "one origin" found a live defect that "one origin" would have made permanent, so it went first
and everything below it moved down one. Recorded here rather than folded quietly into another step, because
it is a vulnerability that predates this ADR and would have outlived it.

`IssuerRateLimits` partitions on `RemoteIpAddress` — which, behind the gateway, **is the gateway**. One bucket
of ten credential requests per minute, for every caller on the platform. That is not a weak limit; it is a
**pre-authentication denial of service on signing in**: ten HTTP requests a minute, from anyone, with no
account and no credentials, and nobody in any clinic can log in.

Proven before it was written down — twelve `POST /connect/login` through Kong from the host gave ten `200`s
and two `429`s; a request from a *different* source IP immediately afterwards got `429`; the same source got
`200` once the window elapsed.

Two independent routes led there, which is why reading either alone would have missed it. In Development,
`UseHbmpTransportSecurity` returns *before* `UseForwardedHeaders` runs. Outside Development it runs, but
`ForwardedHeadersOptions.KnownProxies` defaults to loopback — a gateway on a container network is not
loopback, so the header is silently ignored and the result is identical.

It bears directly on this ADR beyond being adjacent: §4 puts an nginx in front of Kong, making it two hops
instead of one, and §5 and §6 add four more credential endpoints to that same bucket. Shipping them over a
shared partition would have widened a platform-wide outage and called it a login redesign.

---

## 10. Open questions for the reviewer

1. **Should `locked` include the remaining time, or only the fact?** The ADR says `retryAfterSeconds`, on the
   grounds that "try again later" with no number is what makes people phone the clinic manager. The counter-
   argument is that it hands an attacker a precise lockout-window oracle for timing a spray.
2. **Should a first sign-in with no second factor enrolled force enrolment before reaching a portal?**
   Today it does not — the user gets in and is quietly denied protected scopes later by `MfaEvaluator`, which
   surfaces as an unexplained 403 rather than as "you have not finished setting up your account". This ADR
   builds the enrolment screen but does not change when it is compulsory. Making it compulsory is a policy
   decision with a real cost: it puts a mandatory step between a new clinician and the patient in front of
   them.
3. **Does the administrative reset (§6.5) need a break-glass exception** for a user whose email is
   unreachable — a provider-side account with no mailbox? If so it needs its own audited path, not a
   reinstatement of the password-setting endpoint.
