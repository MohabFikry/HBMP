using Mersal.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Identity.Infrastructure;

/// <summary>A membership as the login chooser needs to show it — min-necessary: which organization, and what
/// the person would be there. No PHI, no counts, nothing about other people.</summary>
public sealed record MembershipOption(Guid MembershipId, string TenantId, IReadOnlyList<string> Roles);

/// <summary>
/// 21.1c — resolves which <see cref="TenantMembership"/> a sign-in acts under (design 40 §1, invariant 1).
///
/// Authorization evaluates against the active membership, never the identity, so every token needs exactly
/// one selected membership. One selectable membership auto-selects; several present a chooser. Everything
/// that is not an Active, non-deleted membership is simply not selectable — fail closed (invariant 2).
/// </summary>
public sealed class MembershipService(IdentityStoreDbContext db, TimeProvider clock)
{
    /// <summary>The memberships this identity may act under, with the role names each would carry. Ordered
    /// stably by tenant so the chooser does not reshuffle between visits.</summary>
    public async Task<IReadOnlyList<MembershipOption>> SelectableAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && !m.IsDeleted && m.Status == MembershipStatus.Active)
            .Select(m => new
            {
                m.MembershipId,
                m.TenantId,
                Roles = db.MembershipRoles.Where(mr => mr.MembershipId == m.MembershipId)
                    .Join(db.Roles, mr => mr.RoleId, r => r.Id, (_, r) => r.Name!)
                    .ToList(),
            })
            .OrderBy(m => m.TenantId)
            .ToListAsync(ct);

        return [.. rows.Select(r => new MembershipOption(
            r.MembershipId, r.TenantId,
            [.. r.Roles.Where(n => n is not null).Select(n => n.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)]))];
    }

    /// <summary>
    /// Resolve the membership a token should be minted for.
    ///
    /// <paramref name="requested"/> is the id stamped on the sign-in cookie by the chooser. It is re-validated
    /// against the store on EVERY use rather than trusted: a cookie outlives the membership it names, so a
    /// membership suspended or ended mid-session must stop resolving immediately. Returns null when nothing is
    /// selectable — the caller refuses rather than falling back to "any membership", because silently acting
    /// under a different organization is precisely the confusion this model exists to prevent.
    /// </summary>
    public async Task<TenantMembership?> ResolveAsync(Guid userId, Guid? requested, CancellationToken ct = default)
    {
        var selectable = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && !m.IsDeleted && m.Status == MembershipStatus.Active)
            .OrderBy(m => m.TenantId)
            .ToListAsync(ct);

        if (selectable.Count == 0) return null;
        if (requested is { } id) return selectable.FirstOrDefault(m => m.MembershipId == id);

        // No explicit selection: auto-select ONLY when there is exactly one. With several, refusing forces the
        // chooser — picking "the first" would silently decide which organization someone is acting for.
        return selectable.Count == 1 ? selectable[0] : null;
    }

    /// <summary>
    /// Expand-phase mirror: keep an identity's membership in step with an identity-level role write.
    ///
    /// 0010 backfilled a membership for every user that existed WHEN IT RAN. It is a migration, not a trigger,
    /// so without this every account created or re-roled afterwards would have no membership — and since
    /// 21.1c refuses to mint a token without one, such an account could authenticate and then be denied at
    /// authorize with no way to fix itself. That is a lockout, not a safe failure, so the two representations
    /// are kept in lockstep for as long as both exist.
    ///
    /// This mirrors ONE membership, in the identity's own tenant, with exactly the roles passed in — the same
    /// snapshot-parity rule 0010 followed. It never invents a second tenant: genuine multi-tenant membership
    /// is an administrative act (21.5), not a side effect of setting roles. Deleted with `user_role` itself
    /// when the contract migration lands.
    /// </summary>
    public async Task<TenantMembership> EnsureMirroredAsync(
        ApplicationUser user, IEnumerable<string> roleNames, string actor, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.UserId == user.Id && m.TenantId == user.TenantId && !m.IsDeleted, ct);

        if (membership is null)
        {
            membership = new TenantMembership
            {
                MembershipId = Guid.NewGuid(), UserId = user.Id, TenantId = user.TenantId,
                ProviderId = user.ProviderId, CreatedBy = actor, CreatedAt = now, UpdatedBy = actor, UpdatedAt = now,
            };
            db.Memberships.Add(membership);
        }

        // A deactivated identity keeps its membership but must not be selectable — the same mapping 0010 used,
        // so deactivate/reactivate keeps working through the membership without a second switch to remember.
        membership.Status = user.IsActive ? MembershipStatus.Active : MembershipStatus.Suspended;
        membership.ActivatedAt ??= user.IsActive ? now : null;
        membership.ProviderId = user.ProviderId;
        membership.UpdatedBy = actor;
        membership.UpdatedAt = now;

        var desired = roleNames.Select(r => r.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var wantedIds = await db.Roles.Where(r => r.NormalizedName != null && desired.Contains(r.NormalizedName))
            .Select(r => r.Id).ToListAsync(ct);
        var existing = await db.MembershipRoles.Where(mr => mr.MembershipId == membership.MembershipId).ToListAsync(ct);

        db.MembershipRoles.RemoveRange(existing.Where(mr => !wantedIds.Contains(mr.RoleId)));
        foreach (var id in wantedIds.Where(id => existing.All(mr => mr.RoleId != id)))
            db.MembershipRoles.Add(new MembershipRole
            {
                MembershipId = membership.MembershipId, RoleId = id, GrantedBy = actor, GrantedAt = now,
            });

        db.MembershipHistory.Add(new TenantMembershipHistory
        {
            MembershipId = membership.MembershipId, UserId = membership.UserId, TenantId = membership.TenantId,
            ProviderId = membership.ProviderId, Status = membership.Status.ToString(),
            HomeBranchId = membership.HomeBranchId, IsDeleted = membership.IsDeleted,
            RowVersion = membership.RowVersion, ChangedBy = actor, ChangedAt = now,
            ChangeReason = "mirror of identity-level roles (21.1c expand)",
        });

        await db.SaveChangesAsync(ct);
        return membership;
    }

    /// <summary>The lower-cased role names held THROUGH a membership (not through the identity).</summary>
    public async Task<IReadOnlyList<string>> RolesForAsync(Guid membershipId, CancellationToken ct = default)
    {
        var names = await db.MembershipRoles.AsNoTracking()
            .Where(mr => mr.MembershipId == membershipId)
            .Join(db.Roles, mr => mr.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync(ct);

        return [.. names.Where(n => n is not null).Select(n => n!.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
    }
}
