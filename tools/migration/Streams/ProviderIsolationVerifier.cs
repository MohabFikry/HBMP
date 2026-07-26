using Mersal.Migration.Core;

namespace Mersal.Migration.Streams;

public sealed record IsolationFinding(string UserId, string ExpectedProvider, string LeakedProvider);

public sealed class IsolationResult(IReadOnlyList<IsolationFinding> findings)
{
    public IReadOnlyList<IsolationFinding> Findings { get; } = findings;
    public bool Isolated => Findings.Count == 0;
}

/// <summary>
/// Post-migration isolation check (phase 12.1 STREAM B acceptance): given the loaded provider users
/// and a function that returns the rows a user can actually see, prove every user sees ONLY its own
/// provider's rows — no cross-provider leakage — before any provider user is enabled (../11).
/// Pure over an injected "visible rows" query so it runs both against the in-memory model (unit) and
/// against real RLS (integration).
/// </summary>
public static class ProviderIsolationVerifier
{
    public static IsolationResult Verify(
        IReadOnlyList<ProviderUserRow> users,
        Func<ProviderUserRow, IEnumerable<string>> visibleProviderIdsFor)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(visibleProviderIdsFor);

        var findings = new List<IsolationFinding>();
        foreach (var user in users)
        {
            foreach (var seen in visibleProviderIdsFor(user).Distinct(StringComparer.Ordinal))
            {
                if (!string.Equals(seen, user.ProviderId, StringComparison.Ordinal))
                    findings.Add(new IsolationFinding(user.UserId, user.ProviderId, seen));
            }
        }
        return new IsolationResult(findings);
    }
}
