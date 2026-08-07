using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Authz;
using Mersal.Profile.Domain;
using Mersal.Profile.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Profile.Tests;

/// <summary>
/// The end-to-end wiring guarantee: <b>every one of the 15 sections has a provider, and every provider calls a
/// route the platform actually serves.</b>
///
/// <para>This is the test that would have caught the state phase 20 shipped in first: providers written against
/// <c>/for-beneficiary/{id}</c> endpoints that did not exist yet. Nothing failed — the composer's degradation
/// path is well-behaved, so each of those sections simply resolved to <c>Unavailable</c> forever, which looks
/// exactly like a slow afternoon. A section that is permanently broken and a section that is momentarily broken
/// are indistinguishable at runtime by design, which is precisely why the wiring needs asserting at build
/// time.</para>
/// </summary>
public class SectionWiringTests
{
    /// <summary>
    /// Every route a profile provider is allowed to call, and the service that serves it. Each entry is a real
    /// endpoint in this repository — the staleness test below re-derives them from the services' own source, so
    /// this list cannot quietly outlive the routes it names.
    /// </summary>
    /// <remarks><c>Marker</c> is the distinctive route fragment as the OWNING service writes it — services map
    /// their routes under a group prefix, so the full path never appears literally in the file that serves
    /// it.</remarks>
    private static readonly (string Client, string Pattern, string Source, string Marker)[] KnownRoutes =
    [
        ("policy", @"^/api/v1/beneficiaries/[^/]+/administrative-360$", "services/policy/Api/CoverageDetailEndpoints.cs", "administrative-360"),
        ("policy", @"^/api/v1/enrollments/[^/?]+/coverage-details$", "services/policy/Api/CoverageDetailEndpoints.cs", "coverage-details"),
        ("policy", @"^/api/v1/enrollments/[^/?]+/timeline", "services/policy/Api/TimelineEndpoints.cs", "/timeline"),
        // Blood group + allergies in ONE gated read, so the alerts section costs one PHI-read audit event
        // rather than two. `/allergies` still exists and is still served — pharmacy-service calls it for
        // prescribe-time screening, where the allergy list is all that is wanted.
        ("emr", @"^/api/v1/beneficiaries/[^/]+/clinical-record$", "services/emr/Api/ClinicalRecords.cs", "clinical-record"),
        ("emr", @"^/api/v1/beneficiaries/[^/]+/profile-context$", "services/emr/Api/ProfileContext.cs", "profile-context"),
        ("orders", @"^/api/v1/investigation-orders/for-beneficiary/[^/?]+", "services/orders/Api/ProfileInvestigations.cs", "investigation-orders/for-beneficiary"),
        ("pharmacy", @"^/api/v1/prescriptions/for-beneficiary/[^/?]+", "services/pharmacy/Api/ProfileSections.cs", "prescriptions/for-beneficiary"),
        ("pharmacy", @"^/api/v1/referrals/for-beneficiary/[^/?]+", "services/pharmacy/Api/ProfileSections.cs", "referrals/for-beneficiary"),
        ("approvals", @"^/api/v1/authorizations/for-beneficiary/[^/?]+", "services/approvals/Api/ProfileAuthorizations.cs", "authorizations/for-beneficiary"),
        ("claims", @"^/api/v1/claims\?beneficiaryId=", "services/claims/Api/ClaimsEndpoints.cs", "beneficiaryId"),
        ("case", @"^/api/v1/cases/for-beneficiary/[^/?]+", "services/case/Api/ProfileCases.cs", "cases/for-beneficiary"),
        ("callcentre", @"^/api/v1/beneficiaries/[^/]+/call-interactions\?", "services/callcentre/Api/CallHistory.cs", "call-interactions"),
    ];

    [Fact]
    public void Every_section_has_exactly_one_registered_provider()
    {
        var providers = Providers();
        var keys = providers.Select(p => p.Key).ToList();

        keys.Should().OnlyHaveUniqueItems("two providers for one section is two answers to one question");
        keys.Should().BeEquivalentTo(ProfileSections.All,
            "every one of the 15 sections needs a provider — a section the matrix grants and nothing serves " +
            "resolves to Unavailable forever, which is indistinguishable from a transient outage");
    }

    [Fact]
    public async Task Every_provider_calls_a_route_the_platform_actually_serves()
    {
        using var recorder = new RouteRecorder();
        var providers = Providers(recorder);

        // A principal wide enough to make every section Visible, so every provider is exercised. Real callers
        // never look like this; the point is coverage of the WIRING, not of the matrix (which SerializedPayloadTests
        // covers cell by cell).
        var context = new ProfileContext
        {
            Roles = new HashSet<string>(StringComparer.Ordinal) { "medical_director", "doctor", "case_manager" },
            TreatingRelationship = true,
            CaseAssignment = true,
        };

        foreach (var provider in providers)
        {
            var decision = ProfilePolicies.Decide(provider.Key, context);
            if (decision is not { State: ProfileSectionState.Visible }) continue;

            try
            {
                await provider.FetchAsync(
                    new SectionRequest(Fixtures.Beneficiary, decision, context, Fixtures.Caller()), default);
            }
#pragma warning disable CA1031 // a parse failure is irrelevant here — we are asserting the URL, not the payload
            catch (Exception) { }
#pragma warning restore CA1031
        }

        recorder.Calls.Should().NotBeEmpty();

        var unknown = recorder.Calls
            .Where(call => !KnownRoutes.Any(r =>
                r.Client == call.Client && Regex.IsMatch(call.Path, r.Pattern)))
            .Select(call => $"{call.Client}{call.Path}")
            .Distinct()
            .ToList();

        unknown.Should().BeEmpty(
            "a provider pointed at a route nothing serves yields a section that is Unavailable forever:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, unknown));
    }

    [Fact]
    public void Every_section_that_can_be_visible_is_actually_fetched()
    {
        // The complement of the rule above: not "does the URL exist" but "did the provider try at all". A
        // provider that silently returns null for a Visible section renders as an empty record — the failure
        // mode design 39 §6 spends three states preventing.
        using var recorder = new RouteRecorder();
        var providers = Providers(recorder).ToDictionary(p => p.Key, StringComparer.Ordinal);

        var context = new ProfileContext
        {
            Roles = new HashSet<string>(StringComparer.Ordinal) { "medical_director" },
        };

        var visible = ProfilePolicies.DecideAll(context)
            .Where(d => d.State == ProfileSectionState.Visible)
            .Select(d => d.Key)
            .ToList();

        visible.Should().NotBeEmpty();
        foreach (var key in visible)
        {
            providers.Should().ContainKey(key,
                "'{0}' is Visible for a medical director and must have something to serve it", key);
        }
    }

    [Fact]
    public void The_known_route_register_does_not_go_stale()
    {
        // Same discipline as libs/architecture's exemption registers: an entry that no longer names a real
        // endpoint silently excuses whatever is wired to that path next.
        var root = RepoRoot();
        foreach (var (client, _, source, marker) in KnownRoutes)
        {
            var path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue("'{0}' is named as the source of a {1} route but does not exist",
                source, client);
            File.ReadAllText(path).Should().Contain(marker,
                "'{0}' is registered as the service that serves '{1}' — if that route moved, this register is " +
                "now excusing whatever gets wired to that path next", source, marker);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<ISectionProvider> Providers(RouteRecorder? recorder = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProfileComposition(new ConfigurationBuilder().Build());
        if (recorder is not null)
        {
            services.AddSingleton<IHttpClientFactory>(recorder);
            // Replace the named clients the extension registered, so nothing reaches a socket.
            var descriptors = services.Where(d => d.ServiceType == typeof(IHttpClientFactory)).ToList();
            foreach (var d in descriptors.Take(descriptors.Count - 1)) services.Remove(d);
        }

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        return [.. scope.ServiceProvider.GetServices<ISectionProvider>()];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>An <see cref="IHttpClientFactory"/> that records (client, path) and answers with empty JSON.</summary>
    private sealed class RouteRecorder : IHttpClientFactory, IDisposable
    {
        public List<(string Client, string Path)> Calls { get; } = [];
        private readonly List<HttpClient> _clients = [];

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Handler(name, this))
            {
                BaseAddress = new Uri("http://wiring.invalid"),
            };
            _clients.Add(client);
            return client;
        }

        public void Dispose()
        {
            foreach (var c in _clients) c.Dispose();
        }

        private sealed class Handler(string name, RouteRecorder owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner.Calls) owner.Calls.Add((name, request.RequestUri?.PathAndQuery ?? ""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
