namespace Mersal.Orders.Api;

/// <summary>
/// 31.6 — the response shapes this service returns, written down.
///
/// <para>An anonymous object returned from an endpoint IS a contract; it was simply an unwritten one. A
/// minimal API returning <c>Results.Ok(new { … })</c> publishes no schema, so the OpenAPI drift gate compared
/// the route and the request and passed silently over the body — while the SPA parsed that body with a strict
/// zod schema. Naming them changes no payload: the property names and casing are unchanged, so the JSON is
/// byte-identical.</para>
/// </summary>

/// <summary>A coded reason an amendment or withdrawal may cite, in both languages.</summary>
/// <remarks>
/// Bilingual because the picker renders in the user's language while the CODE is what is stored and audited:
/// a reason chosen in Arabic and reported in English has to be the same fact.
/// </remarks>
public sealed record AmendmentReasonView(string Code, string NameEn, string NameAr);

/// <summary>
/// One request for access to a restricted result, as the approver's inbox shows it.
/// </summary>
/// <remarks>
/// DELIBERATELY CLINICAL-FREE (design 37 §6). The inbox says who asked, for which line and why; it never
/// carries the result itself. An approver deciding whether someone may read a report must not be shown the
/// report in order to decide.
/// </remarks>
/// <param name="RequestedForRole">Who the access is FOR, which may not be who asked.</param>
/// <param name="RequestedTtlHours">How long the grant would last — a decision input, not a detail.</param>
public sealed record ReportAccessRequestView(
    Guid RequestId,
    Guid OrderId,
    Guid OrderLineId,
    Guid BeneficiaryId,
    string? RequestedBy,
    string? RequestedForRole,
    string PurposeCode,
    string? Justification,
    int RequestedTtlHours,
    string Status,
    DateTimeOffset CreatedAt,
    /// <summary>
    /// May THIS caller decide this request (32.4)?
    /// </summary>
    /// <remarks>
    /// Answered by the server because it is an authorization question. The screen needs it to know which
    /// controls to offer, and a client that worked it out by comparing a subject id against
    /// <see cref="RequestedBy"/> would be deciding authority in a browser.
    /// </remarks>
    bool CanDecide = false,
    /// <summary>Did this caller raise it? The requester acts through supply-info, never through decide.</summary>
    bool IsRequester = false);

/// <summary>A request's identity and where it now stands — the answer to every act on one.</summary>
public sealed record ReportAccessStatusView(Guid RequestId, string Status);

/// <summary>An approval that also minted a grant, so the caller learns when it expires without asking again.</summary>
public sealed record ReportAccessGrantView(
    Guid RequestId, string Status, Guid GrantId, DateTimeOffset ExpiresAt);

/// <summary>
/// A restricted result the caller may know EXISTS but not read (design 37 §6).
/// </summary>
/// <remarks>
/// The existence-only projection. It carries no value, no reference range and no report — nothing that is
/// withheld is present in the payload and hidden by the client, because a field that reaches the browser has
/// been disclosed whatever the browser does with it.
/// </remarks>
public sealed record RestrictedResultView(
    bool Restricted,
    Guid OrderId,
    Guid LineId,
    string SensitivityLevel,
    string Category,
    string Status,
    Guid? OrderingBranchId);
