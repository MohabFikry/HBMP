using Mersal.Authz;

namespace Mersal.Profile.Domain;

/// <summary>
/// The caller's OWN credentials, forwarded verbatim to every owning service.
///
/// <para>This type exists so the alternative is conspicuous by its absence: there is no
/// <c>ServiceAccountCredentials</c>, no client-secret field, nowhere for a privileged token to enter the
/// composition path. Design 39 §7.2 — compose under the caller's token, never a service account — is the
/// invariant an aggregator most easily breaks, because a service account makes every downstream call succeed
/// and the resulting profile looks complete rather than correct.</para>
/// </summary>
public sealed record CallerCredentials(string Authorization, string? ActiveBranch, string? CorrelationId)
{
    public bool IsBearer =>
        Authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Authorization.Length > 7;
}

/// <summary>What a provider is being asked for: which beneficiary, in which projection, for which caller.</summary>
public sealed record SectionRequest(
    Guid BeneficiaryId,
    SectionDecision Decision,
    ProfileContext Context,
    CallerCredentials Caller);

/// <summary>
/// One section, one owning service. Providers OWN NO DATA — each is a call to the service that does, under the
/// caller's token, so that service applies its own authorization exactly as it would to a direct request
/// (design 39 §1: two independent layers, neither sufficient alone).
/// </summary>
/// <remarks>Return <c>null</c> for "nothing exists" → NotApplicable. THROW for "could not answer" → Unavailable.
/// Returning an empty section to signal a failure is the one thing a provider must never do.</remarks>
public interface ISectionProvider
{
    string Key { get; }

    Task<object?> FetchAsync(SectionRequest request, CancellationToken ct);
}

/// <summary>Timeouts for the fan-out. The context bar is on every clinical screen, so its budget is the tight
/// one (build prompt 20.5: p95 &lt; 400ms for header+alerts, &lt; 2.5s for the full profile).</summary>
public sealed record ProfileCompositionOptions
{
    /// <summary>Per-section timeout. One slow service degrades ITS section, never the profile.</summary>
    public TimeSpan SectionTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Overall budget across the whole fan-out.</summary>
    public TimeSpan OverallBudget { get; init; } = TimeSpan.FromMilliseconds(2500);
}

/// <summary>What was served, for the ProfileViewed audit event. Withheld sections are named, because "the
/// profile was opened" without "and these sections were withheld" cannot answer an access review.</summary>
public sealed record CompositionReport(
    IReadOnlyList<string> Served,
    IReadOnlyList<string> Withheld,
    IReadOnlyList<string> Unavailable)
{
    /// <summary>A compact <c>key:state</c> list for the audit event's outcome field.</summary>
    public static string Describe(IReadOnlyList<ProfileSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return string.Join(',', sections.Select(s => $"{s.Key}:{s.State}"));
    }
}

public sealed record ProfileCompositionResult(PatientProfile Profile, CompositionReport Report);
