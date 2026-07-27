using Mersal.Authz;

namespace Mersal.Profile.Domain;

/// <summary>
/// The composition engine (build prompt 20.1).
///
/// <para>Orchestration only — it holds no <c>HttpClient</c> and no <c>DbContext</c>, so the whole of the
/// security-relevant behaviour (gate before fetch, project after fetch, degrade one section not the profile) is
/// unit-testable against fake providers without a network or a database. The HTTP lives in the providers.</para>
///
/// <para><b>Order of operations, and why.</b> The matrix decides FIRST, and a section the caller can never see
/// is never fetched: it is cheaper, and it means the owning service is never even asked about a patient on
/// behalf of someone with no business asking. Only then does the fan-out happen, and only then is the payload
/// narrowed to the variant. Fetch-then-filter would work too, right up until the day the filter is skipped.</para>
/// </summary>
public sealed class ProfileComposer(
    IEnumerable<ISectionProvider> providers,
    ProfileCompositionOptions options,
    TimeProvider clock)
{
    private readonly IReadOnlyDictionary<string, ISectionProvider> _providers =
        providers.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public async Task<ProfileCompositionResult> ComposeAsync(
        Guid beneficiaryId,
        ProfileContext context,
        IReadOnlyCollection<string>? requestedSections,
        CallerCredentials caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caller);

        // Invariant 2, enforced at runtime and not only by the architecture test: without the caller's own
        // bearer there is nothing to compose under, and composing anyway is the vulnerability.
        if (!caller.IsBearer)
            throw new InvalidOperationException(
                "The patient profile composes under the CALLER'S token. No caller bearer was present, and a " +
                "service-account fallback does not exist by design (design 39 §7.2).");

        // A caller may ask for a subset (the context bar asks for header+alerts). The matrix still decides:
        // asking for a section you may not see gets you the same nothing as not asking.
        var wanted = requestedSections is { Count: > 0 }
            ? new HashSet<string>(requestedSections, StringComparer.Ordinal)
            : null;

        var decisions = ProfilePolicies.DecideAll(context)
            .Where(d => wanted is null || wanted.Contains(d.Key))
            .ToList();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.OverallBudget);

        // Parallel fan-out. Each section is independent; a slow one must not serialize behind a fast one on a
        // screen a clinician opens between patients.
        var fetched = await Task.WhenAll(decisions.Select(d => ResolveAsync(d, beneficiaryId, context, caller, budget.Token)));

        var sections = fetched.ToList();
        var report = new CompositionReport(
            [.. sections.Where(s => s.State == nameof(ProfileSectionState.Visible)).Select(s => s.Key)],
            [.. sections.Where(s => s.State == nameof(ProfileSectionState.Restricted)).Select(s => s.Key)],
            [.. sections.Where(s => s.State == nameof(ProfileSectionState.Unavailable)).Select(s => s.Key)]);

        return new ProfileCompositionResult(
            new PatientProfile(beneficiaryId, clock.GetUtcNow(), sections), report);
    }

    private async Task<ProfileSection> ResolveAsync(
        SectionDecision decision, Guid beneficiaryId, ProfileContext context,
        CallerCredentials caller, CancellationToken ct)
    {
        // Withheld by the matrix: no fetch at all. The reason travels to the user so they can request access
        // rather than conclude the record is empty (design 39 §7.5).
        if (!decision.ShouldFetch)
        {
            return decision.State switch
            {
                ProfileSectionState.Restricted => ProfileSection.Restricted(
                    decision.Key,
                    decision.ReasonCode ?? ProfileReasons.RoleNotPermitted,
                    RequestAccessFor(decision, beneficiaryId)),
                _ => ProfileSection.NotApplicable(decision.Key),
            };
        }

        if (!_providers.TryGetValue(decision.Key, out var provider))
        {
            // The matrix grants a section nothing is registered to serve. That is a wiring bug, and reporting it
            // as Unavailable is honest: the caller is entitled to it and did not get it.
            return ProfileSection.Unavailable(decision.Key, "no-provider-registered");
        }

        try
        {
            using var perSection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perSection.CancelAfter(options.SectionTimeout);

            var data = await provider.FetchAsync(
                new SectionRequest(beneficiaryId, decision, context, caller), perSection.Token);

            if (data is null) return ProfileSection.NotApplicable(decision.Key);

            var projected = SectionProjection.Apply(
                decision.Key, data, decision.Variant, ProfilePhotoAccess.MayView([.. context.Roles]));

            // The projector dropped a payload it did not recognise — treat it as unavailable rather than serving
            // an empty section, which would read as "no records" (see ProfileSection.Unavailable).
            return projected is null
                ? ProfileSection.Unavailable(decision.Key, "unprojectable-payload")
                : ProfileSection.Visible(decision.Key, projected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The SECTION's own timeout fired — it degrades, the rest of the profile still renders (design 39
            // §6). The guard matters: when the OVERALL budget or the client's disconnect cancelled us, this is
            // not a per-section failure and pretending otherwise would return a profile nobody is waiting for.
            return ProfileSection.Unavailable(decision.Key, "timeout");
        }
        catch (OperationCanceledException)
        {
            return ProfileSection.Unavailable(decision.Key, "budget-exceeded");
        }
#pragma warning disable CA1031 // one failing upstream must never fail the whole profile — that is the design
        catch (Exception)
#pragma warning restore CA1031
        {
            return ProfileSection.Unavailable(decision.Key, "upstream-error");
        }
    }

    /// <summary>Offer the way out of a Restricted state, where one exists. A non-treating clinician and a
    /// missing sensitive-result grant both have a real request path; "your role does not include this" does
    /// not, and offering a button that cannot succeed is worse than offering none.</summary>
    private static RequestAccessAction? RequestAccessFor(SectionDecision decision, Guid beneficiaryId) =>
        decision.ReasonCode switch
        {
            ProfileReasons.NotTreating or ProfileReasons.SensitiveRequiresGrant =>
                RequestAccessAction.SensitiveResult(beneficiaryId),
            _ => null,
        };
}
