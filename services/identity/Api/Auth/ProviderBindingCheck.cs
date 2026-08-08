using Mersal.Authz;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// At startup: report every ACTIVE membership that holds a provider-scoped role and is bound to no provider.
///
/// <para><b>The failure this makes loud.</b> A pharmacist, lab tech or imaging tech whose membership carries
/// no <c>provider_id</c> signs in perfectly, receives every scope their role grants, and is then refused
/// every screen in their own portal — each provider-scoped gate opens by rejecting a caller with no provider,
/// before any rule is evaluated. The refusal is accurate ("you are not associated with a dispensing
/// pharmacy") and reads as a permissions bug, so the actual cause — a person configured to work nowhere — is
/// the last thing anyone looks at. Three roles were in that state at once and nothing said so.</para>
///
/// <para><b>Why it warns and does not throw.</b> This is a data problem, and identity-service is the one
/// service every other login depends on. Refusing to start would take the whole platform down to protest a
/// misconfigured pharmacist, which is strictly worse than the 403 it is warning about. The log line names
/// each account and what to do, which is the part that was missing.</para>
///
/// <para>The roles are derived from the authorization rules (<see cref="ProviderScopedRoles"/>), not listed
/// here, so a new provider-scoped rule is covered the day it is written.</para>
/// </summary>
public sealed class ProviderBindingCheck(IServiceProvider services, ILogger<ProviderBindingCheck> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var scoped = ProviderScopedRoles.All;
        if (scoped.Count == 0)
        {
            // Not "nothing to check" — the rules are compiled in, so an empty set means the derivation broke.
            log.LogWarning("no provider-scoped roles were derived from the policy rules; " +
                           "the provider-binding check cannot run and provider-bound logins are unverified");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityStoreDbContext>();

        List<(string User, string Tenant, string Role)> unbound;
        try
        {
            // Deleted and non-Active memberships are excluded: a membership nobody can act under cannot
            // produce the 403 this exists to predict.
            var rows = await db.Memberships.AsNoTracking()
                .Where(m => m.ProviderId == null && !m.IsDeleted && m.Status == MembershipStatus.Active)
                .Join(db.MembershipRoles.AsNoTracking(), m => m.MembershipId, mr => mr.MembershipId, (m, mr) => new { m, mr })
                .Join(db.Roles.AsNoTracking(), x => x.mr.RoleId, r => r.Id, (x, r) => new { x.m, RoleName = r.Name })
                .Join(db.Users.AsNoTracking(), x => x.m.UserId, u => u.Id,
                    (x, u) => new { User = u.UserName, x.m.TenantId, x.RoleName })
                .ToListAsync(ct);

            // Role matching happens here rather than in SQL: the comparison is lower-cased (as
            // RolesForAsync normalises, and as the policy rules spell them) and the set comes from compiled
            // rules, so pushing it into the query would mean shipping the whole set as parameters to save
            // nothing — there are a few dozen memberships, not a few million.
            unbound = [.. rows
                .Where(r => r.RoleName is not null && scoped.Contains(r.RoleName.ToLowerInvariant()))
                .Select(r => (User: r.User ?? "(unnamed)", Tenant: r.TenantId, Role: r.RoleName!.ToLowerInvariant()))
                .Distinct()
                .OrderBy(r => r.User, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A check that cannot run must say so rather than pass silently — a quiet failure here would
            // read as "every account is bound", which is the reassurance this whole class exists to deny.
            log.LogWarning(ex, "the provider-binding check could not query memberships; provider-bound logins are unverified");
            return;
        }

        if (unbound.Count == 0)
        {
            log.LogInformation(
                "Provider-binding check: every active membership holding a provider-scoped role ({Roles}) is bound to a provider.",
                string.Join(", ", scoped.OrderBy(r => r, StringComparer.Ordinal)));
            return;
        }

        foreach (var (user, tenant, role) in unbound)
        {
            log.LogError(
                "PROVIDER-SCOPED LOGIN IS UNUSABLE: '{User}' holds role '{Role}' in tenant {Tenant} but the "
                + "membership has no provider_id. This account will authenticate and then be refused every "
                + "screen in its portal with a 403 that names a permissions problem. Bind it to a provider "
                + "(admin surface: PATCH /admin/memberships/{{id}}; dev: "
                + "tools/dev/seed-provider-bound-accounts.sql), then have the user sign out and back in — "
                + "the claim is stamped at sign-in.",
                user, role, tenant);
        }

        log.LogError("Provider-binding check: {Count} unusable provider-scoped login(s).", unbound.Count);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
