namespace Mersal.Patient.Api;

/// <summary>
/// 31.6 — the paged envelopes this service returns.
///
/// <para><b>The ITEMS are deliberately free-form, and that is the contract.</b> Every beneficiary row is
/// disclosed through <c>BeneficiaryReadGuard</c>, which projects field by field against the caller's role:
/// a receptionist and a doctor asking the same question get different KEYS, not merely different values.
/// Declaring a fixed item schema here would publish a shape no caller reliably receives — a lie that reads
/// as documentation.</para>
///
/// <para>What IS fixed is the envelope, and it is worth declaring on its own: a client cannot page without
/// knowing that <c>page</c>, <c>pageSize</c> and <c>items</c> are there.</para>
/// </summary>
/// <param name="Items">
/// One dictionary per beneficiary, carrying only the fields this caller may see (minimum-necessary,
/// 18-security-model). A key that is absent was withheld, not empty.
/// </param>
public sealed record DisclosedPageView(
    int Page, int PageSize, IReadOnlyList<IReadOnlyDictionary<string, object?>> Items);

/// <summary>A paged envelope that also states the size of the whole queue.</summary>
/// <param name="Total">
/// The queue, not the page. A pager that can only say "next" cannot tell an approver how much work is left,
/// which is the question they actually have.
/// </param>
public sealed record DisclosedQueueView(int Page, int PageSize, int Total, IReadOnlyList<object> Items);

/// <summary>A registration's identity and where it stands, returned when one is raised.</summary>
public sealed record RegistrationCreatedView(Guid RegistrationId, string Status);
