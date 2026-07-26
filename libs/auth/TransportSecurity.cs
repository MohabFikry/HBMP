using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;

namespace Mersal.Auth;

/// <summary>Transport hardening shared by every HBMP service (audit H8 / 16.5). Outside Development the
/// service enforces HSTS + HTTPS redirection. Because services run behind a TLS-terminating gateway
/// (Kong) / mesh (Linkerd) and listen on plain http:8080 internally, we first honour the edge's
/// <c>X-Forwarded-Proto</c> so the redirect reflects the real client scheme instead of redirect-looping
/// on the container's http listener. In Development (http Keycloak, no TLS) this is a no-op.</summary>
public static class TransportSecurity
{
    public static WebApplication UseHbmpTransportSecurity(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
            return app;

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
        });
        app.UseHsts();
        app.UseHttpsRedirection();
        return app;
    }
}
