using System.Reflection;

namespace Mersal.Authz;

/// <summary>
/// The roles whose authority is bounded by a PROVIDER — a pharmacist dispenses for their pharmacy, a lab tech
/// fulfils for their lab — derived from the policy rules themselves rather than listed by hand.
///
/// <para><b>Why this exists.</b> A member of one of these roles whose membership carries no
/// <c>provider_id</c> can authenticate perfectly, hold every scope their role grants, and then be refused
/// every screen in their own portal: each provider-scoped gate opens with "you are not associated with a
/// dispensing pharmacy / a fulfilling provider" before any rule is evaluated. That refusal is accurate and
/// reads as a permissions bug, so the real cause — a person configured to work nowhere — is the last thing
/// anyone checks. It went unnoticed for `pharmacist`, `lab_tech` and `imaging_tech` simultaneously.</para>
///
/// <para><b>Why by reflection and not a list.</b> A hand-maintained list is a second opinion about a question
/// the rules already answer, and it would be correct exactly until someone adds the next provider-scoped
/// rule. The rules are the source of truth: any role appearing on a rule that requires
/// <see cref="AbacConditions.ProviderOwnership"/> needs a provider, by construction.</para>
/// </summary>
public static class ProviderScopedRoles
{
    private static readonly Lazy<IReadOnlySet<string>> Cache = new(Discover, isThreadSafe: true);

    /// <summary>Every role that appears on at least one provider-ownership rule, across all policy bundles.</summary>
    public static IReadOnlySet<string> All => Cache.Value;

    /// <summary>Does this role require a provider to be able to do its job?</summary>
    public static bool Requires(string role) => All.Contains(role);

    /// <summary>Which of these roles are provider-scoped? Case-sensitive, as roles are everywhere else.</summary>
    public static IReadOnlyList<string> IntersectWith(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return [.. roles.Where(All.Contains).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Invokes every zero-argument static rule factory in this assembly and keeps the roles attached to
    /// provider-ownership rules.
    ///
    /// <para>BOTH factory shapes are walked, and that is not a detail. Most policy classes expose
    /// <c>Rules()</c> returning a list; <see cref="DefaultPolicies"/> exposes <c>Bundle()</c> returning a
    /// <see cref="PolicyBundle"/> that wraps one — and it holds the provider-ownership rule for
    /// <c>lab_tech</c>/<c>imaging_tech</c> on <c>order_line</c>. A walk that recognised only the first shape
    /// would quietly under-report, which is the precise failure this class exists to prevent, one level
    /// up.</para>
    ///
    /// <para>Otherwise constrained deliberately: zero parameters, and a return type that is either shape.
    /// That is narrow enough that reflection cannot wander into a method doing something else. A factory
    /// that throws is not swallowed — a policy bundle that cannot be built is a fault worth surfacing here
    /// rather than at the first request.</para>
    /// </summary>
    private static IReadOnlySet<string> Discover()
    {
        var factories = typeof(ProviderScopedRoles).Assembly.GetTypes()
            // Static classes only: abstract + sealed is how the CLR represents `static class`.
            .Where(t => t is { IsAbstract: true, IsSealed: true, IsPublic: true })
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetParameters().Length == 0
                        && (typeof(IEnumerable<PolicyRule>).IsAssignableFrom(m.ReturnType)
                            || typeof(PolicyBundle).IsAssignableFrom(m.ReturnType)));

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            var produced = factory.Invoke(null, null);
            IEnumerable<PolicyRule> rules = produced switch
            {
                PolicyBundle bundle => bundle.Rules,
                IEnumerable<PolicyRule> list => list,
                _ => [],
            };

            foreach (var rule in rules)
            {
                if (!rule.RequiredConditions.Contains(AbacConditions.ProviderOwnership, StringComparer.Ordinal))
                    continue;
                // A rule with NO roles grants to any authenticated role, so it says nothing about which
                // roles are provider-scoped and must not be read as "all of them".
                foreach (var role in rule.Roles) roles.Add(role);
            }
        }
        return roles;
    }
}
