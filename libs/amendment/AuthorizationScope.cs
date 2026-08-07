namespace Mersal.Amendment;

/// <summary>What an amendment does to the authorisation the order was carrying.</summary>
public enum AuthorizationImpact
{
    /// <summary>The order carried no authorisation. There is nothing to invalidate and nobody to trouble —
    /// most orders are not gated, and reporting these as "beyond scope" would flood the approval queue with
    /// items no reviewer ever saw.</summary>
    NotAuthorized,

    /// <summary>Inside what was approved. The authorisation REMAINS VALID; the approval team is not told.</summary>
    WithinApprovedScope,

    /// <summary>Outside it. The authorisation's basis no longer holds, so the order returns to pending
    /// authorisation and approvals is notified with a before/after.</summary>
    BeyondApprovedScope,
}

/// <param name="Codes">The itemised approved codes. EMPTY means the approval did not itemise — a
/// whole-order approval — and constrains nothing by code. It does NOT mean "nothing is approved".</param>
/// <param name="Quantity">The approved quantity, or null when the reviewer named none.</param>
/// <param name="DurationDays">The approved duration, or null when the reviewer named none.</param>
public readonly record struct ApprovedScope(
    IReadOnlyCollection<string> Codes, decimal? Quantity, int? DurationDays);

/// <summary>The line as amended — what is being compared against the approval.</summary>
public readonly record struct AmendedScope(string Code, decimal Quantity, int? DurationDays);

/// <summary>
/// 30.4 — design 46 §5: whether an amendment needs re-approval depends on ONE question — does it stay inside
/// what was approved?
///
/// <para><b>Getting it backwards is costly in both directions.</b> Treat every amendment as re-approvable and
/// the queue floods, which teaches reviewers to rubber-stamp; treat none as re-approvable and you have built
/// a way to obtain an approval for one thing and dispense another. Both directions are tested.</para>
///
/// <para><b>Why the subset predicate lives here and not in approvals.</b> The phase-30 prompt asks that this
/// reuse <c>DecisionRules.ValidatePartialScope</c> rather than adding a second comparator — but orders and
/// pharmacy cannot reference approvals' Domain, and a runtime HTTP call would make a doctor's ability to
/// correct a mistake depend on approvals-service being reachable. So the predicate moved DOWN into this pure
/// library and <c>ValidatePartialScope</c> now calls it: there is genuinely one notion of "inside the
/// approved set", used by the reviewer's partial-approval check and by both amendment paths, and approvals'
/// own tests still assert its behaviour unchanged.</para>
/// </summary>
public static class AuthorizationScope
{
    /// <summary>
    /// Is every candidate code inside the approved set? Order-insensitive, de-duplicated, and ORDINAL —
    /// a service code is an identifier, and a culture-sensitive or case-insensitive comparison would equate
    /// codes master data treats as distinct.
    /// </summary>
    public static bool IsSubsetOfApproved(
        IReadOnlyCollection<string> candidate, IReadOnlyCollection<string> approved) =>
        new HashSet<string>(candidate, StringComparer.Ordinal)
            .IsSubsetOf(new HashSet<string>(approved, StringComparer.Ordinal));

    /// <summary>
    /// Assess one amended line against the approval its order carried.
    ///
    /// <para>Each dimension is only a constraint when the reviewer actually set it. An approval that named a
    /// code but no number approved the CODE, not an amount, and inventing a ceiling they did not set would
    /// refuse amendments nobody objected to.</para>
    /// </summary>
    public static AuthorizationImpact Assess(AmendedScope amended, ApprovedScope? approved)
    {
        if (approved is not { } scope) return AuthorizationImpact.NotAuthorized;

        // An EMPTY code set is an approval that did not itemise — a whole-order approval — so it constrains
        // nothing by code. Reading it as an empty allow-list would send every amendment of every approved
        // order back to the queue, which is the flooding failure mode in its purest form.
        if (scope.Codes.Count > 0 && !IsSubsetOfApproved([amended.Code], scope.Codes))
            return AuthorizationImpact.BeyondApprovedScope;

        // A different service in a SMALLER amount is still a different service; the code is checked first and
        // independently, so a falling quantity can never wave through a substitution nobody reviewed.
        if (scope.Quantity is { } approvedQty && amended.Quantity > approvedQty)
            return AuthorizationImpact.BeyondApprovedScope;

        if (scope.DurationDays is { } approvedDays && amended.DurationDays is { } days && days > approvedDays)
            return AuthorizationImpact.BeyondApprovedScope;

        return AuthorizationImpact.WithinApprovedScope;
    }
}
