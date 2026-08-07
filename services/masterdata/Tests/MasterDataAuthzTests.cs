using FluentAssertions;
using Mersal.Auth.Authorization;
using Mersal.MasterData.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Mersal.MasterData.Tests;

/// <summary>
/// Phase 18.E2 — masterdata-service's authorization suite, revised in phase 26.1.
///
/// <para>
/// WHAT CHANGED, AND WHY THE OLD REASONING IS LEFT LEGIBLE. Through phase 25 this suite argued that
/// masterdata was the one service where a BARE <c>RequireAuthorization()</c> was the right answer, and warned
/// that someone would eventually "harden" it into a scope check and break every clinical screen. Phase 26.1
/// made that change deliberately, so the warning is answered here rather than deleted.
/// </para>
/// <para>
/// The old argument was: the catalogue is public medical reference data — ICD-10, CPT, LOINC, ATC, drugs,
/// interactions, allergens, examination types — tenant-free by design, carrying no PHI and nothing to
/// isolate. Every clinical role legitimately reads it: a doctor ordering, a pharmacist checking an
/// interaction, a claims officer resolving a billed code. Requiring a scope would mean granting it to
/// essentially everyone, which is a control in name only.
/// </para>
/// <para>
/// Every sentence of that is still true, and <c>masterdata:read</c> is accordingly granted to every role that
/// holds any scope at all — it restricts no clinician, and the "break every clinical screen" risk was met by
/// granting from the existing role set rather than an enumerated list. What the scope adds is not
/// restriction. Reference-data reach becomes a stated, reviewable, revocable line in the role matrix instead
/// of an unstated consequence of holding a token; a service, integration or partner token must ASK for the
/// catalogue rather than receive it by default; and phase 27's <c>approval_supervisor</c> has something real
/// to be granted. An unscoped endpoint is an unbounded one, and phase 26 puts a 22,653-product typeahead on
/// this surface.
/// </para>
/// <para>
/// Unchanged and still asserted: the catalogue is not anonymous (it is a fingerprint of what this platform
/// treats), and no WRITE path exists on it. Master data changes through admin-service's governed,
/// effective-dated, audited path (8b.2), never through this service.
/// </para>
/// </summary>
public class MasterDataAuthzTests : IClassFixture<MasterDataAuthzTests.Host>
{
    private readonly Host _host;
    public MasterDataAuthzTests(Host host) => _host = host;

    public sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MasterData"] = "Host=localhost;Port=1;Database=x;Username=x;Password=x",
                ["Events:UseInMemoryOutbox"] = "true",
            }));
            // Route metadata is built at startup and needs no database; the seeders do.
            builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        }

        public IReadOnlyList<RouteEndpoint> Endpoints() =>
        [.. Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }

    private static string Path(RouteEndpoint e) => "/" + e.RoutePattern.RawText?.TrimStart('/');

    private static readonly Dictionary<string, string> Anonymous = new(StringComparer.Ordinal)
    {
        ["/health/live"] = "liveness probe — a gated probe cannot report a dead service",
        ["/health/ready"] = "readiness probe — kubelet carries no bearer token, so a gated probe never reports Ready",
        ["/metrics"] = "Prometheus scrape, in-cluster only",
    };

    [Fact]
    public void The_catalogue_is_authenticated_not_anonymous()
    {
        // Not PHI, but not public either: the set of codes a platform carries reveals what it treats, and
        // an open catalogue is a free target map for anyone probing the estate.
        var open = _host.Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Path).Distinct().Order(StringComparer.Ordinal).ToList();

        open.Should().BeSubsetOf(Anonymous.Keys);
    }

    [Fact]
    public void Every_api_route_requires_a_token()
    {
        var ungated = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(Path).Distinct().ToList();

        ungated.Should().BeEmpty("unauthenticated reads of the reference catalogue:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, ungated));
    }

    [Fact]
    public void The_reference_catalogue_exposes_no_write_path()
    {
        // The real control on master data. Codes are safety-critical — a wrong ICD mapping misroutes a
        // diagnosis, a wrong ATC entry breaks interaction checking — so changes go through admin-service's
        // effective-dated, versioned, audited governance path (8b.2). A POST here would bypass all of it.
        var writes = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is { } m
                        && m.HttpMethods.Any(h => h is "POST" or "PUT" or "PATCH" or "DELETE"))
            .Select(e => $"{string.Join('/', e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {Path(e)}")
            .Distinct().Order(StringComparer.Ordinal).ToList();

        // POST-shaped READS. These take a list of codes or ids in the body — too long and too structured for
        // a query string — and return a computed answer without touching a row. `/resolve` maps a code to
        // its canonical entry; `/check` runs an interaction or allergy screen; `/by-ids` (26.3) returns the
        // indications for a set of drugs; `/by-codes` (ADR-0034) returns list prices for a set of
        // examinations, keyed on code because an order line always carries one and only carries an
        // examination-type id if it was written after phase 14.6. HTTP has no verb for "read with a body",
        // so POST is correct here and none of them is a write.
        //
        // The list is a SUFFIX allow-list, which is the point: a new POST has to be named for what it does
        // and added here deliberately. Anything else — /examination-types, /drugs, a bare resource path —
        // fails this test, which is how a write would be caught.
        // `/ancestors` (28.7) is the newest of these: it takes the encounter's diagnosis codes and returns
        // each one's chain up the ICD-10 tree, so the indication check can ask "is this diagnosis underneath
        // that indication" instead of truncating both to three characters. It reads `icd_ancestor`, a
        // closure the LOADER builds; nothing here can write to it.
        string[] readShapedPost = ["/resolve", "/check", "/by-ids", "/by-codes", "/ancestors"];
        var mutating = writes
            .Where(w => !readShapedPost.Any(suffix => w.Contains(suffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        mutating.Should().BeEmpty(
            "master data must change only through admin-service's governed path (8b.2), never here:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, mutating));
    }

    [Fact]
    public void Every_api_route_requires_the_masterdata_read_scope()
    {
        // The phase-26.1 change. A route that carries authorization metadata but no scope policy is the
        // state this service was in for eight phases: authenticated, and unbounded.
        var unscoped = _host.Endpoints()
            .Where(e => Path(e).StartsWith("/api/v1", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>()?.Policy
                        != HbmpPolicies.Scope(MasterDataScopes.Read))
            .Select(Path).Distinct().Order(StringComparer.Ordinal).ToList();

        unscoped.Should().BeEmpty("every catalogue read is gated on {0}:{1}{2}",
            MasterDataScopes.Read, Environment.NewLine, string.Join(Environment.NewLine, unscoped));
    }

    [Fact]
    public void The_service_actually_serves_the_catalogue_it_claims_to()
    {
        // Guards the guard: an empty route table would make all three tests above vacuously green.
        var paths = _host.Endpoints().Select(Path).ToList();
        paths.Count(p => p.StartsWith("/api/v1", StringComparison.Ordinal))
            .Should().BeGreaterThan(15, "masterdata serves ICD/CPT/LOINC/ATC/drugs/interactions/allergens");
    }
}
