using System.Text.Json;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Infrastructure;

/// <summary>
/// policy-service's administrative-360 (design 38 §4.6), fetched ONCE per request and shared by the four
/// sections derived from it: header, coverage, documents and notes.
///
/// <para>Design 39 §2 says the administrative 360 "becomes the administrative sections" — so this is that
/// consolidation, and the memoization is the reason it is a consolidation rather than four copies of the same
/// call. Four independent providers hitting the same endpoint would quadruple the load AND quadruple the PHI-read
/// audit events policy-service writes, which would make the audit trail read as four accesses where a user made
/// one.</para>
///
/// <para>Scoped per request, so the memo lives exactly one request — one caller, one patient, one authorization
/// context. Phase 18's X9 lesson: a cache keyed on fewer dimensions than the decision depends on is a breach,
/// not a bug. This one is not keyed at all, because its lifetime IS the key.</para>
/// </summary>
public sealed class AdministrativeSource(CallerScopedHttp http) : IDisposable
{
    public const string ClientName = "policy";

    private readonly object _sync = new();
    private Task<JsonDocument?>? _fetch;

    public async Task<JsonElement?> GetAsync(Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
    {
        Task<JsonDocument?> fetch;
        lock (_sync)
        {
            // Started inside the lock but awaited outside it: the providers fan out in parallel, so the second
            // one through must join the first one's call rather than issue a second.
            _fetch ??= http.GetAsync(
                ClientName, $"/api/v1/beneficiaries/{beneficiaryId}/administrative-360", caller, ct);
            fetch = _fetch;
        }

        // A failure replays to every awaiter, so one upstream failure produces four honest Unavailable sections
        // rather than one Unavailable and three silently empty.
        var document = await fetch;
        return document?.RootElement;
    }

    public void Dispose()
    {
        if (_fetch is { IsCompletedSuccessfully: true }) _fetch.Result?.Dispose();
    }
}
