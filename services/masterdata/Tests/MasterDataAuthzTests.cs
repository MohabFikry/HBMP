using FluentAssertions;
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
/// Phase 18.E2 — masterdata-service's first authorization suite. It serves 21 endpoints and had one test
/// file, covering a mapper.
///
/// The judgement this suite records is worth stating, because it is not "add more tests": masterdata is the
/// one service where a BARE <c>RequireAuthorization()</c> is the right answer, and that needs to be written
/// down or someone will eventually "harden" it into a scope check and break every clinical screen.
///
/// It serves ICD-10, CPT, LOINC, ATC, drugs, interactions, allergens and examination types. That is a public
/// medical reference catalogue — the same codes are in every clinical system on earth — and it is tenant-FREE
/// by design: a diagnosis code means the same thing for every tenant. There is no PHI here, no financial
/// data, and nothing to isolate. Every clinical role legitimately needs to read it: a doctor ordering, a
/// pharmacist checking an interaction, a claims officer resolving a billed code. Requiring a scope would
/// mean granting that scope to essentially everyone, which is a control in name only.
///
/// What DOES matter, and is asserted here: authentication is required (the catalogue is not anonymous —
/// it is a fingerprint of what this platform treats), and no WRITE path exists on it. Master data changes
/// through admin-service's governed, effective-dated, audited path (8b.2), never through this service.
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

        // POST-shaped READS. These take a list of codes in the body — too long and too structured for a
        // query string — and return a computed answer without touching a row. `/resolve` maps a code to its
        // canonical entry; `/check` runs an interaction or allergy screen. HTTP has no verb for "read with a
        // body", so POST is correct here and none of them is a write.
        string[] readShapedPost = ["/resolve", "/check"];
        var mutating = writes
            .Where(w => !readShapedPost.Any(suffix => w.Contains(suffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        mutating.Should().BeEmpty(
            "master data must change only through admin-service's governed path (8b.2), never here:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, mutating));
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
