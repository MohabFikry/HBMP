using Mersal.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>
/// The THIRD gate as an endpoint filter (design 40 §4, adaptation A4). Reads the `features` claim, which
/// identity-service resolves once per token from its projection of admin.tenant_feature (21.4 propagation).
///
/// <para><b>It runs after authorization, by construction.</b> An endpoint filter executes inside the endpoint's
/// pipeline, which is reached only once the authorization middleware has admitted the request — so an
/// unauthenticated caller still gets 401 from the pipeline and never sees a programme message, and a caller who
/// lacks the scope still gets the authorization denial. Enablement is asked last, and only of someone who was
/// otherwise going to be let through.</para>
///
/// <para><b>It can only subtract.</b> There is no branch here that admits a request the endpoint's own policy
/// would have refused. That is deliberate and is the reason enablement is a separate filter rather than another
/// clause in a policy: a policy that could say yes is a policy someone will eventually reach for to hand out
/// access.</para>
///
/// <para><b>A principal with no tenant is not subject to it.</b> Enablement answers "is THIS ORGANISATION
/// onboarded onto this programme", and a client-credentials token — the event pipeline's ingest calls — belongs
/// to no organisation, so the question has no answer for it. Refusing it would not be a policy decision; it
/// would stop the platform's own machinery for every tenant at once. The carve-out costs nothing: that token
/// holds exactly four narrow `*:ingest`/`*:project` scopes and cannot reach a worklist or a decision. Every
/// principal that DOES carry a tenant is evaluated, with no exceptions — <c>ProgramFeatureFilterTests</c>
/// pins that so the carve-out cannot quietly widen.</para>
///
/// <para>Do not place a gate on an ADMINISTRATION route: gating the screen that switches a feature back on
/// would make a tenant with everything off unrecoverable.</para>
/// </summary>
public sealed class ProgramFeatureFilter(string featureKey) : IEndpointFilter
{
    private readonly string _featureKey = !string.IsNullOrWhiteSpace(featureKey)
        ? featureKey
        : throw new ArgumentException("A feature key is required.", nameof(featureKey));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var user = context.HttpContext.User;

        // No tenant → no organisation → nothing to ask. See the class note.
        if (string.IsNullOrWhiteSpace(user.FindFirst(HbmpClaimTypes.TenantId)?.Value))
            return await next(context);

        // Read the claim with the SAME reader HbmpPrincipal uses, rather than building a whole principal:
        // FromClaims throws when there is no `sub`, and a gate that can throw turns a missing claim into a 500
        // instead of a refusal. Same shape rules (repeated claims or a JSON array), no failure mode.
        var features = TokenClaims.ExtractMulti(user, HbmpClaimTypes.Features);

        return features.Contains(_featureKey, StringComparer.Ordinal)
            ? await next(context)
            : ProgramEnablement.NotEnabled(_featureKey);
    }
}

public static class ProgramFeatureEndpointExtensions
{
    /// <summary>
    /// Refuse this endpoint (or group) unless the caller's tenant is onboarded onto <paramref name="featureKey"/>.
    ///
    /// <para>Apply to the module's FUNCTIONAL routes; not to the administration surface — see
    /// <see cref="ProgramFeatureFilter"/>. Health probes and machine-to-machine ingest need no exemption: the
    /// first is anonymous, the second carries no tenant.</para>
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string featureKey)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(new ProgramFeatureFilter(featureKey));
        return builder;
    }
}
