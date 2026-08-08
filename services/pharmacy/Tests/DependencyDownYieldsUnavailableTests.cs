using System.Net;
using System.Text;
using FluentAssertions;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Api;
using Mersal.Pharmacy.Infrastructure;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// The test phase 26 exists for (doc 43 §1 rule 1, invariant 2; registered in
/// docs/quality/invariant-registry.yaml).
/// </summary>
/// <remarks>
/// <para>
/// Until phase 26 the prescribing screener caught every <c>HttpRequestException</c> and returned no alerts,
/// and treated every non-2xx response the same way through a bare <c>if (resp.IsSuccessStatusCode)</c> —
/// six such paths across three calls. An outage therefore rendered to the prescriber as a clean bill of
/// health, and doc 43 calls it the single most dangerous line in the prescribing path.
/// </para>
/// <para>
/// These tests kill the dependency in each way it can realistically die and assert the same thing every
/// time: the result is <c>Unavailable</c>, and it is never <c>Ok</c>. The 403 case is not hypothetical —
/// phase 26.1 put masterdata behind <c>masterdata:read</c>, so a token missing the scope now takes exactly
/// that path.
/// </para>
/// </remarks>
public class DependencyDownYieldsUnavailableTests
{
    private static readonly Guid Beneficiary = Guid.NewGuid();
    private static readonly Guid DrugA = Guid.NewGuid();
    private static readonly Guid DrugB = Guid.NewGuid();

    public static TheoryData<string, HttpMessageHandler> DeadDependencies() => new()
    {
        { "connection refused", new ThrowingHandler(new HttpRequestException("Connection refused")) },
        { "500 from the service", new StatusHandler(HttpStatusCode.InternalServerError) },
        { "403 — token lacks masterdata:read", new StatusHandler(HttpStatusCode.Forbidden) },
        { "404 — endpoint not deployed", new StatusHandler(HttpStatusCode.NotFound) },
        { "502 from the gateway", new StatusHandler(HttpStatusCode.BadGateway) },
        { "unreadable body", new BodyHandler("this is not json") },
    };

    [Theory]
    [MemberData(nameof(DeadDependencies))]
    public async Task A_dead_dependency_yields_Unavailable_and_NEVER_Ok(string scenario, HttpMessageHandler handler)
    {
        var ports = Ports(handler);

        var snapshot = await ports.FetchAsync(Beneficiary, [DrugA, DrugB], encounterId: null, clientDiagnoses: null, "token");
        var request = new ValidationRequest(
            Guid.NewGuid(),
            [new PrescriptionLineInput(Guid.NewGuid(), DrugA, "Drug A"),
             new PrescriptionLineInput(Guid.NewGuid(), DrugB, "Drug B")],
            []);

        var result = PrescriptionValidator.Validate(request, snapshot, DateTimeOffset.UtcNow);

        // The three checks that are fetched over HTTP. Dose is excluded deliberately: there is no dosing
        // service to be down, so it reports "no dosing rule configured" whatever the network is doing —
        // NotChecked, which is also not Ok.
        var fetched = result.Findings
            .Where(f => f.Kind is CheckKind.Indication or CheckKind.Interaction or CheckKind.Allergy)
            .ToList();

        fetched.Should().NotBeEmpty();
        fetched.Should().OnlyContain(f => f.State == CheckState.Unavailable,
            "every fetched source is behind the dead dependency ({0})", scenario);

        // The invariant itself, over every check without exception.
        result.Findings.Should().NotContain(f => f.State == CheckState.Ok,
            "'{0}' must never render as a clean result", scenario);

        foreach (var line in request.Lines)
        {
            result.StateFor(line.LineId).Should().Be(CheckState.Unavailable);
        }
    }

    [Theory]
    [MemberData(nameof(DeadDependencies))]
    public async Task The_screener_reports_the_unavailable_checks_rather_than_staying_silent(
        string scenario, HttpMessageHandler handler)
    {
        // The legacy screening path, which the prescribe endpoint still calls. Its old implementation
        // returned an EMPTY alert list here — indistinguishable from "screened clean".
        var screener = new ValidatorBackedPrescribingScreener(Ports(handler), TimeProvider.System);

        var screening = await screener.ScreenAsync(Beneficiary, [DrugA, DrugB], "token");

        screening.HasUnavailableChecks.Should().BeTrue("'{0}' left checks unrun and that must be visible", scenario);
        screening.Alerts.Should().Contain(a => a.Kind == Domain.AlertKind.Unavailable);
    }

    [Fact]
    public async Task A_partial_outage_does_not_round_the_healthy_checks_up_to_clean()
    {
        // masterdata answers, emr does not. The allergy check is the one that could not run, and saying so
        // is the whole difference between an honest screen and a false assurance.
        var ports = Ports(new RoutingHandler(
            masterdata: new BodyHandler("""{"interactions":[],"knownPairCount":42,"items":[]}"""),
            emr: new ThrowingHandler(new HttpRequestException("emr down"))));

        var snapshot = await ports.FetchAsync(Beneficiary, [DrugA], encounterId: null, clientDiagnoses: null, "token");
        var line = new PrescriptionLineInput(Guid.NewGuid(), DrugA, "Drug A");
        var result = PrescriptionValidator.Validate(
            new ValidationRequest(Guid.NewGuid(), [line], []), snapshot, DateTimeOffset.UtcNow);

        result.For(CheckKind.Interaction).State.Should().Be(CheckState.Ok, "masterdata answered");
        result.For(CheckKind.Allergy).State.Should().Be(CheckState.Unavailable, "emr did not");
        result.StateFor(line.LineId).Should().Be(CheckState.Unavailable,
            "a line with an unchecked source is not adequately summarised by the checks that did run");
    }

    [Fact]
    public async Task A_timeout_is_Unavailable_not_a_hung_encounter()
    {
        // No retry or circuit breaker exists anywhere on this platform, so an unbounded wait would leave a
        // doctor on a spinner mid-consultation. A deadline turns that into an answer.
        var ports = Ports(new HangingHandler());

        var snapshot = await ports.FetchAsync(Beneficiary, [DrugA], encounterId: null, clientDiagnoses: null, "token");

        snapshot.Interactions.Should().BeOfType<Fetched<InteractionTable>.Unavailable>();
        ((Fetched<InteractionTable>.Unavailable)snapshot.Interactions).Reason
            .Should().Contain("did not respond");
    }

    private static HttpClinicalValidationPorts Ports(HttpMessageHandler handler) =>
        new(new StubFactory(handler), new NotYetImplementedBenefitPreCheck(TimeProvider.System),
            new NoLabelSource(), TimeProvider.System);

    /// <summary>
    /// Stands in for openFDA so these tests stay offline.
    /// </summary>
    /// <remarks>
    /// Reports the source as unavailable rather than returning empty evidence. This suite exists to prove
    /// that a dependency being down never reads as a clean result, and a label stub that quietly said
    /// "nothing found" would be the very defect under test.
    /// </remarks>
    private sealed class NoLabelSource : IDrugLabelSource
    {
        public Task<Fetched<LabelEvidence>> FetchAsync(
            IReadOnlyList<DrugIngredient> drugs, CancellationToken ct = default)
            => Task.FromResult(Fetched.NotAvailable<LabelEvidence>("no label source in this test"));
    }

    // ---------------------------------------------------------------- stubs

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri($"https://{name}.test") };
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
    }

    private sealed class BodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Never answers — the dependency that is up but wedged.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Routes by host so one service can be healthy while another is not.</summary>
    private sealed class RoutingHandler(HttpMessageHandler masterdata, HttpMessageHandler emr) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var inner = r.RequestUri!.Host.StartsWith("emr", StringComparison.Ordinal) ? emr : masterdata;
            return new HttpMessageInvoker(inner, disposeHandler: false).SendAsync(r, ct);
        }
    }
}

internal static class FindingLookup
{
    /// <summary>The single finding of a kind, when the test has exactly one line.</summary>
    public static Finding For(this ValidationResult result, CheckKind kind) =>
        result.Findings.Single(f => f.Kind == kind);
}
