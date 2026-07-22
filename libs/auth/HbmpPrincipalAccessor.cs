using Microsoft.AspNetCore.Http;

namespace Mersal.Auth;

/// <summary>Resolves the current <see cref="HbmpPrincipal"/> for the in-flight request.</summary>
public interface IHbmpPrincipalAccessor
{
    /// <summary>The authenticated principal, or null when the request is unauthenticated.</summary>
    HbmpPrincipal? Principal { get; }

    /// <summary>The authenticated principal or throws if unauthenticated.</summary>
    HbmpPrincipal Require();
}

public sealed class HbmpPrincipalAccessor(IHttpContextAccessor httpContextAccessor) : IHbmpPrincipalAccessor
{
    public HbmpPrincipal? Principal
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? HbmpPrincipal.FromClaims(user) : null;
        }
    }

    public HbmpPrincipal Require() =>
        Principal ?? throw new InvalidOperationException("No authenticated principal on the current request.");
}
