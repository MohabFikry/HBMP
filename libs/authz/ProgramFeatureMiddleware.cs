using Mersal.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>
/// Applies the programme gate to a WHOLE SERVICE, for the services that ARE one module (claims-service is the
/// claims programme; there is no part of it a tenant without claims should reach).
///
/// <para><b>Why a middleware rather than a filter on each route group.</b> These services declare their groups
/// across a dozen files — claims alone has four — so gating each one is a dozen chances to miss one, and the
/// endpoint added next year defaults to UNGATED. Here the default inverts: everything under the service is
/// gated, and each exemption is named on one line that a reviewer can see all of at once. Where a service hosts
/// more than one programme's worth of surface (reporting: extracts are gated, dashboards are not) the
/// per-endpoint <see cref="ProgramFeatureEndpointExtensions.RequireFeature"/> is the right tool instead.</para>
///
/// <para><b>Register it AFTER <c>UseAuthorization()</c>.</b> That is what makes enablement the LAST question
/// (design 40 §4: after authorization, before execution). Registered earlier, a caller who lacks the scope —
/// or who is not signed in — would be told their organisation is not on the programme, which sends them to
/// Mersal for something their own administrator refuses, and is the precise confusion the separate problem type
/// exists to prevent.</para>
/// </summary>
public static class ProgramFeatureMiddleware
{
    /// <summary>
    /// Refuse every request this service serves unless the caller's tenant is onboarded onto
    /// <paramref name="featureKey"/>.
    ///
    /// <para>Two kinds of request pass through untouched, both because enablement is a question about an
    /// authenticated ORGANISATION. <b>Unauthenticated</b> ones — the health probes, in practice; this does mean
    /// an endpoint that forgot <c>RequireAuthorization</c> would slip the gate, but such an endpoint is already
    /// serving data to anonymous callers, a far larger defect than an ungated module. And <b>tenant-less</b>
    /// ones: the client-credentials token behind the event pipeline's ingest calls belongs to no organisation,
    /// and refusing it would stop the platform's own machinery for every tenant rather than enforce a policy.
    /// That token holds four narrow ingest/project scopes and can reach nothing else. Every principal that
    /// carries a tenant is evaluated.</para>
    /// </summary>
    /// <param name="exemptPathPrefixes">
    /// Paths this gate must NOT cover, matched case-insensitively as prefixes. Reserved for surface that must
    /// keep working for a tenant whose programme is OFF. Ingest routes do NOT belong here — they are already
    /// covered by the tenant-less carve-out above, and a path exemption would also uncover any human-facing
    /// endpoint that happens to share the prefix (approvals' ingest sits on the same path as its worklist).
    /// </param>
    public static IApplicationBuilder UseProgramFeature(
        this IApplicationBuilder app, string featureKey, params string[] exemptPathPrefixes)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.IsNullOrWhiteSpace(featureKey))
            throw new ArgumentException("A feature key is required.", nameof(featureKey));

        var exempt = exemptPathPrefixes ?? [];

        return app.Use(async (context, next) =>
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                await next(context);
                return;
            }

            // No tenant → no organisation → nothing to ask. This is what keeps the event pipeline working.
            if (string.IsNullOrWhiteSpace(context.User.FindFirst(HbmpClaimTypes.TenantId)?.Value))
            {
                await next(context);
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;
            foreach (var prefix in exempt)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    await next(context);
                    return;
                }
            }

            // Same reader HbmpPrincipal uses. Not FromClaims: it throws without a `sub`, and a gate that can
            // throw answers a missing claim with 500 instead of a refusal.
            var features = TokenClaims.ExtractMulti(context.User, HbmpClaimTypes.Features);
            if (features.Contains(featureKey, StringComparer.Ordinal))
            {
                await next(context);
                return;
            }

            await ProgramEnablement.NotEnabled(featureKey).ExecuteAsync(context);
        });
    }
}
