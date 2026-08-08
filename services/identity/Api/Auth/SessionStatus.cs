using Mersal.Identity.Infrastructure;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// The closed set of answers the first-party sign-in endpoints give (ADR-0036 §5, phase 28.3).
///
/// <para>
/// ============================================================================================================
/// WHY A STATUS AND NOT A TOKEN
/// ============================================================================================================
/// Signing in here is up to four steps, not one: password → TOTP → membership selection, with lockout and
/// deactivation alongside. The obvious shortcut — post the credentials to <c>/connect/token</c> and get a
/// token back — cannot express any of that, because <b>the token endpoint has no way to say "now give me your
/// TOTP code"</b>. Its response is a token or an OAuth error. Every step after the password would have to be
/// crammed into an error code and re-derived by the client, or — far more likely, being the path of least
/// resistance — quietly stop being part of signing in. That is not a login redesign; it is the removal of MFA
/// from a platform whose admin scopes and break-glass are gated on it.
/// </para>
/// <para>
/// So these endpoints establish the ordinary ASP.NET Identity session cookie and report WHERE IN THE SEQUENCE
/// the caller now is. The token is minted afterwards by the unchanged authorization-code + PKCE flow.
/// </para>
/// </summary>
public static class SessionStatus
{
    /// <summary>Every factor is satisfied and a membership resolves. The caller may now run the silent
    /// authorize.</summary>
    public const string Authenticated = "authenticated";

    /// <summary>Password accepted; the second factor is outstanding. NOT an outcome yet — nothing is recorded
    /// against the sign-in history until the second factor resolves it, or every successful MFA login would
    /// file a "failure" and the history would be least readable for the best-protected accounts.</summary>
    public const string TwoFactorRequired = "two_factor_required";

    /// <summary>Authenticated, but the identity holds more than one selectable membership (ADR-0021). The
    /// options travel with this response — information about the account, disclosed only to somebody who has
    /// already presented every factor it has.</summary>
    public const string MembershipSelectionRequired = "membership_selection_required";

    /// <summary>
    /// Authenticated, and may act nowhere.
    ///
    /// <para>
    /// A SIXTH status, added while building §5's five, because the alternative was worse. This state is
    /// reachable only with correct credentials, so it leaks nothing an attacker could use — and folding it
    /// into <see cref="InvalidCredentials"/> would tell somebody whose password was exactly right that it was
    /// wrong, sending them to reset a password that was never the problem. That is the same mistake §5.2
    /// refuses to make for lockout, arriving from a different direction.
    /// </para>
    /// </summary>
    public const string NoMembership = "no_membership";

    /// <summary>Temporarily locked. Distinguished on purpose (§5.2): the alternative sends a locked-out nurse
    /// to reset a password that was never wrong, and the reset does not unlock the account.</summary>
    public const string Locked = "locked";

    /// <summary>Everything else — unknown username, wrong password, deactivated account, wrong TOTP code. The
    /// internal distinction survives in the audit record and dies here.</summary>
    public const string InvalidCredentials = "invalid_credentials";
}

// The chooser's options are Mersal.Identity.Infrastructure.MembershipOption — the type MembershipService
// already returns. A second record of the same shape here would compile, read identically, and drift the
// first time one of them gained a field.

/// <summary>
/// The reply shape for every sign-in step.
///
/// <para>
/// It carries no token, no user id, no display name and no roles. A caller learns exactly one thing — what to
/// do next — until they have satisfied every factor, at which point the membership options appear because
/// choosing between them is the next thing to do.
/// </para>
/// </summary>
public sealed record SessionStatusResponse(
    string Status,
    int? RetryAfterSeconds = null,
    IReadOnlyList<MembershipOption>? Memberships = null,
    /// <summary>
    /// A FRESH antiforgery request token, to use on the next step.
    ///
    /// <para>
    /// Not a convenience. ASP.NET Core binds an antiforgery token to the authenticated user, so the token
    /// fetched while anonymous stops validating the moment the password step signs somebody in — and the next
    /// call in the same sequence (the second factor, the membership choice) is refused with a 400 that looks
    /// like a bug in the client. Found exactly that way: the two membership tests failed at the chooser while
    /// every step before it passed.
    /// </para>
    /// <para>
    /// The alternative was a documented rule that the client must re-fetch after each step — a rule which is
    /// invisible when broken, since it only bites on the sequences that have more than one step, which are
    /// the sequences with a second factor. Handing the next token back with each reply cannot be forgotten.
    /// This is the REQUEST half of the double-submit pair; the cookie half stays HttpOnly and never appears
    /// in a body.
    /// </para>
    /// </summary>
    string? Csrf = null,
    /// <summary>Whether an authenticator is enrolled. Reported on an AUTHENTICATED response only, so the SPA
    /// can offer enrolment — deliberately NOT a status of its own, because enrolment is not currently required
    /// to sign in (ADR-0036 §10 leaves that open). Today an unenrolled user gets in and is denied protected
    /// scopes later by MfaEvaluator, which surfaces as an unexplained 403 rather than as "your account setup
    /// is unfinished"; this field is what lets the UI say the second thing without changing the rule.</summary>
    bool? TwoFactorEnrolled = null);
