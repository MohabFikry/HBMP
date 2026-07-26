using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Mersal.Authz;

/// <summary>Runtime break-glass provider (16.6, H5): resolves the caller's active grants from admin-service
/// (<c>GET /api/v1/admin/break-glass/active</c>, the caller's own bearer forwarded) so an admin-approved grant
/// actually widens access at the point of decision — replacing <see cref="NullBreakGlassProvider"/> which never
/// elevated. Results are cached per subject for a short TTL to keep the per-request authz check cheap.
///
/// FAIL-CLOSED: no HTTP context, no bearer, a non-success response, or any exception ⇒ NO grant (access is not
/// widened). Break-glass only ever *adds* access, so failing closed can never over-expose. The synchronous
/// <see cref="IBreakGlassProvider.ActiveGrantFor"/> is honoured with <c>HttpClient.Send</c> (no sync-over-async).</summary>
public sealed class HttpBreakGlassProvider(
    IHttpClientFactory factory,
    IHttpContextAccessor http,
    IMemoryCache cache,
    ILogger<HttpBreakGlassProvider> log) : IBreakGlassProvider
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    public const string HttpClientName = "break-glass";

    public BreakGlassGrant? ActiveGrantFor(HbmpRequestContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.SubjectUserId)) return null;
        var grants = cache.GetOrCreate($"break-glass:{ctx.SubjectUserId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return Fetch(ctx.SubjectUserId);
        }) ?? [];
        return grants.FirstOrDefault(g => g.Covers(ctx.Resource, ctx.Now));
    }

    private IReadOnlyList<BreakGlassGrant> Fetch(string subject)
    {
        try
        {
            var token = ExtractBearer();
            if (token is null) return [];  // no forwarded identity ⇒ fail closed

            var client = factory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/break-glass/active");
            req.Headers.Authorization = new("Bearer", token);

            using var resp = client.Send(req);
            if (!resp.IsSuccessStatusCode) return [];

            var dtos = resp.Content.ReadFromJsonAsync<List<GrantDto>>().GetAwaiter().GetResult() ?? [];
            return dtos.Select(d => d.ToGrant(subject)).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "break-glass lookup failed for {Subject}; failing closed (no elevation)", subject);
            return [];
        }
    }

    private string? ExtractBearer()
    {
        var header = http.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)) return null;
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : header;
    }

    private sealed record GrantDto(Guid GrantId, DateTimeOffset NotBefore, DateTimeOffset ExpiresAt,
        List<string>? ScopedResourceTypes, List<string>? ScopedResourceIds)
    {
        public BreakGlassGrant ToGrant(string subject) => new()
        {
            GrantId = GrantId.ToString(), SubjectUserId = subject, ApprovedByUserId = "admin-service",
            NotBefore = NotBefore, ExpiresAt = ExpiresAt,
            ScopedResourceTypes = new HashSet<string>(ScopedResourceTypes ?? [], StringComparer.Ordinal),
            ScopedResourceIds = new HashSet<string>(ScopedResourceIds ?? [], StringComparer.Ordinal),
        };
    }
}

public static class BreakGlassRegistration
{
    /// <summary>Wire the live break-glass provider, replacing the null one AddHbmpAuthorization registers. Call
    /// AFTER AddHbmpAuthorization. AdminBaseUrl resolves from BreakGlass:AdminBaseUrl → Siblings:Admin → default.</summary>
    public static IServiceCollection AddHbmpBreakGlass(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        var adminBase = config["BreakGlass:AdminBaseUrl"] ?? config["Siblings:Admin"] ?? "http://admin-service:8080";
        services.AddHttpClient(HttpBreakGlassProvider.HttpClientName, c =>
        {
            c.BaseAddress = new Uri(adminBase);
            c.Timeout = TimeSpan.FromSeconds(2);  // short — a slow admin never blocks a request beyond this
        });
        services.RemoveAll<IBreakGlassProvider>();
        services.AddSingleton<IBreakGlassProvider, HttpBreakGlassProvider>();
        return services;
    }
}
