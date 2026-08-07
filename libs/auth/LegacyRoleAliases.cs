namespace Mersal.Auth;

/// <summary>
/// 29.1 — the dual-accept table for role names being renamed, and the ONE place a rename is allowed to be
/// ambiguous (design 45 §1, ADR-0029).
///
/// <para><b>Why this exists at all.</b> Renaming a role is not atomic on a running platform. Two artefacts
/// outlive the deployment that renames it:</para>
/// <list type="number">
/// <item>An <b>unexpired access token</b> minted before the switch carries the OLD role name and stays valid
/// for the rest of its TTL (300 s — docs/security/token-contract.md §4). Every request it makes in that
/// window reaches code that only knows the new name.</item>
/// <item>Services are <b>independently deployable</b> (CLAUDE.md § Repository layout), so during a rollout a
/// token minted by the already-switched issuer reaches a service that has not been redeployed yet and only
/// knows the OLD name.</item>
/// </list>
///
/// <para><b>So the expansion is bidirectional, not a one-way normalisation.</b> A one-way map (legacy →
/// canonical) fixes case 1 and breaks case 2, and case 2 is the one that happens on every rollout rather than
/// only on the first 300 s of one. During the window the principal's role set carries BOTH spellings, so a
/// check written against either name answers the same question. That is the entire point: the rename becomes
/// invisible to the ~40 call sites that compare role names, instead of each of them needing its own dual
/// check that someone must later remember to remove.</para>
///
/// <para><b>This is a window, not a permanent alias.</b> The CONTRACT step empties <see cref="Aliases"/>,
/// after which only the canonical name resolves. Until then both do. Contrast
/// <c>Mersal.Audit.LegacyIdentifierDisplay</c>, which IS permanent — historical audit rows are hash-linked and
/// can never be rewritten, so their old spellings are resolved for display forever.</para>
///
/// <para><b>What this does NOT do.</b> It never invents authority. An alias maps a name to a name; the role
/// still has to have been granted, and the scope behind it still has to be present on the token. Adding an
/// entry here can only make an already-granted role answer to a second spelling.</para>
/// </summary>
public static class LegacyRoleAliases
{
    /// <summary>Legacy name → canonical name, for renames still inside their dual-accept window.
    ///
    /// <para>EMPTY THIS in the contract step, never edit an entry in place: an entry that changes meaning
    /// silently re-points every token in flight.</para></summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 29.1 — design 45 §1. Expand: 0031_radiology_role_expand.sql. Contract: the deferred migration
            // services/identity/Infrastructure/Migrations/deferred/0032_radiology_role_contract.sql, which is
            // NOT applied by tools/ci/apply-migrations.sh — see docs/runbooks/radiology-rename.md.
            ["imaging_tech"] = "radiology_tech",
        };

    /// <summary>Canonical → legacy, derived so the two directions can never disagree.</summary>
    private static readonly IReadOnlyDictionary<string, string> Reverse =
        Aliases.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>True while any rename is still inside its dual-accept window. False once contracted.</summary>
    public static bool WindowOpen => Aliases.Count > 0;

    /// <summary>The canonical spelling of <paramref name="role"/>, or the role itself when it is not aliased.
    /// Use this when you need exactly one name — writing a row, keying a dictionary, comparing for equality
    /// in NEW code. Never use it to decide authority; use the expanded set for that.</summary>
    public static string Canonical(string role) =>
        Aliases.TryGetValue(role, out var canonical) ? canonical : role;

    /// <summary>
    /// Expand a role set so every aliased name is present in BOTH spellings.
    ///
    /// <para>Applied once, at the token→principal boundary (<see cref="TokenClaims.ExtractRoles"/>), so that
    /// every downstream comparison — libs/authz policy sets, <c>ProviderCapability</c>, the SoD matrix, the
    /// branch-scope role lists — keeps working unchanged whichever spelling the token carries.</para>
    /// </summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            expanded.Add(role);
            if (Aliases.TryGetValue(role, out var canonical)) expanded.Add(canonical);
            else if (Reverse.TryGetValue(role, out var legacy)) expanded.Add(legacy);
        }
        return expanded;
    }
}
