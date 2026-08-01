using System.Net;
using System.Text;
using FluentAssertions;
using Mersal.Inventory.Api;
using Mersal.Inventory.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mersal.Inventory.Tests;

/// <summary>
/// The transport half of the D5 guard, against a stubbed masterdata-service.
///
/// <para><b>Why these are separate from the endpoint tests.</b> Those substitute
/// <see cref="IMedicinesDirectory"/> wholesale, which is right — they are testing what the endpoint DOES with
/// each verdict. But it leaves the code that PRODUCES the verdict untested, and that code is where every
/// fail-closed rule actually lives. A directory that quietly returned "not a medicine" on a 500, a timeout or
/// a contract change would satisfy every endpoint test in the suite while the guard stopped guarding.</para>
///
/// <para>Each case below is a way the call can fail to answer, and every one of them must come back
/// <see cref="MedicineVerdict.DirectoryUnreachable"/> — never <see cref="MedicineVerdict.NotAMedicine"/>.
/// "Could not ask" and "asked, and it is fine" are the two answers it is most tempting to collapse.</para>
/// </summary>
public class MedicinesDirectoryTests
{
    private static HttpMedicinesDirectory Directory(HttpMessageHandler handler, string? bearer = null)
    {
        var ctx = new DefaultHttpContext();
        if (bearer is not null) ctx.Request.Headers.Authorization = bearer;
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://masterdata.test") };
        return new HttpMedicinesDirectory(http, accessor, NullLogger<HttpMedicinesDirectory>.Instance);
    }

    private static StubHandler Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task A_MATCH_COMES_BACK_AS_A_MEDICINE_WITH_THE_FIELDS_NEEDED_TO_NAME_IT()
    {
        // The endpoint puts the drug code and name into the refusal, so a coordinator is told WHICH medicine
        // they matched. Losing them here turns an actionable refusal into "computer says no".
        var d = Directory(Json("""
            {"matched":true,"drugCode":"HEPB-20","name":"Hepatitis B Vaccine","atcCode":"J07BC01","isVaccine":true}
            """));

        var result = await d.ClassifyAsync("VAX-1", "Hepatitis B Vaccine 20mcg/ml", "لقاح");

        result.Verdict.Should().Be(MedicineVerdict.IsAMedicine);
        result.DrugCode.Should().Be("HEPB-20");
        result.DrugName.Should().Be("Hepatitis B Vaccine");
        result.AtcCode.Should().Be("J07BC01");
        result.IsVaccine.Should().BeTrue();
    }

    [Fact]
    public async Task AND_A_NON_MATCH_LETS_THE_CONSUMABLE_THROUGH()
    {
        // The negation. Without it every assertion in this file is satisfied by a directory that calls
        // everything a medicine, which would refuse the entire clinic catalogue.
        var d = Directory(Json("""{"matched":false,"drugCode":null,"name":null,"atcCode":null,"isVaccine":false}"""));

        (await d.ClassifyAsync("GZ-1", "Gauze swab", "شاش")).Verdict.Should().Be(MedicineVerdict.NotAMedicine);
    }

    [Fact]
    public async Task A_200_THAT_DESERIALIZES_TO_NOTHING_IS_UNREACHABLE_NOT_A_NO()
    {
        // The subtle one, and the reason the tri-state exists. `null` parses as a valid JSON document, so a
        // contract drift or an empty body arrives looking exactly like success. Treating it as "not a
        // medicine" would open the gate through the single path that never looks like a failure.
        var d = Directory(Json("null"));

        (await d.ClassifyAsync("VAX-1", "Hepatitis B Vaccine", null)).Verdict
            .Should().Be(MedicineVerdict.DirectoryUnreachable);
    }

    [Fact]
    public async Task SO_IS_A_MALFORMED_BODY()
    {
        var d = Directory(Json("{ this is not json"));

        (await d.ClassifyAsync("VAX-1", "Hepatitis B Vaccine", null)).Verdict
            .Should().Be(MedicineVerdict.DirectoryUnreachable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AND_SO_IS_ANY_NON_SUCCESS_STATUS(HttpStatusCode code)
    {
        // 401 is in this list deliberately. If the bearer forwarding ever breaks, masterdata refuses the
        // caller rather than the item — and the failure must NOT read as "no medicine found".
        var d = Directory(Json("""{"matched":false}""", code));

        (await d.ClassifyAsync("VAX-1", "Hepatitis B Vaccine", null)).Verdict
            .Should().Be(MedicineVerdict.DirectoryUnreachable);
    }

    [Fact]
    public async Task A_DEAD_SERVICE_IS_UNREACHABLE_RATHER_THAN_AN_UNHANDLED_EXCEPTION()
    {
        // Letting this throw would surface as a 500 from item creation. That fails closed too, by accident —
        // but it fails closed with a stack trace instead of "retry shortly", and the next person to see it
        // fixes it by catching and returning NotAMedicine.
        var d = Directory(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        (await d.ClassifyAsync("GZ-1", "Gauze swab", null)).Verdict
            .Should().Be(MedicineVerdict.DirectoryUnreachable);
    }

    [Fact]
    public async Task A_TIMEOUT_IS_UNREACHABLE_TOO()
    {
        var d = Directory(new StubHandler(_ => throw new TaskCanceledException("timed out")));

        (await d.ClassifyAsync("GZ-1", "Gauze swab", null)).Verdict
            .Should().Be(MedicineVerdict.DirectoryUnreachable);
    }

    [Fact]
    public async Task THE_CALLERS_BEARER_IS_FORWARDED_AND_THE_ITEM_IS_SENT_AS_QUERY_PARAMETERS()
    {
        // masterdata must authorize the SAME principal, not a service identity with wider reach. And the
        // request carries a SKU and a name — reference data about a thing. Asserted because this is the call
        // that would be the natural place to start attaching an encounter id, and inventory carrying nothing
        // patient-shaped is invariant 9.
        HttpRequestMessage? seen = null;
        var d = Directory(new StubHandler(r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"matched":false}""", Encoding.UTF8, "application/json") };
        }), bearer: "Bearer tok-123");

        // The name deliberately contains '&' and '='. An item called "Gauze 10x10 & tape" built into the
        // query unescaped would split into a bogus extra parameter and truncate the name — so the classify
        // would run against "Gauze 10x10 " and miss a match it should have made. Escaping is the guard's
        // correctness here, not tidiness.
        await d.ClassifyAsync("GZ-1", "Gauze 10x10 & tape = set", "شاش");

        seen.Should().NotBeNull();
        seen!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        seen.Headers.Authorization.Parameter.Should().Be("tok-123");

        // AbsoluteUri, not ToString(): ToString() UNESCAPES, so it would report a passing assertion against
        // a directory that never escaped anything at all.
        var uri = seen.RequestUri!.AbsoluteUri;
        uri.Should().Contain("/api/v1/drugs/classify");
        uri.Should().Contain("code=GZ-1");
        uri.Should().Contain(Uri.EscapeDataString("Gauze 10x10 & tape = set"));
        uri.Should().NotContain("tape = set", "an unescaped name would split the query string");
    }

    [Fact]
    public async Task AN_ANONYMOUS_CALL_STILL_ASKS_RATHER_THAN_SKIPPING_THE_CHECK()
    {
        // No bearer on the inbound request — the outbound one goes anyway, unauthenticated, and masterdata
        // decides. The alternative (skip the call when there is no token) would make "no auth header" a way
        // past the guard.
        HttpRequestMessage? seen = null;
        var d = Directory(new StubHandler(r =>
        {
            seen = r;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"matched":false}""", Encoding.UTF8, "application/json") };
        }));

        await d.ClassifyAsync("GZ-1", "Gauze", null);

        seen.Should().NotBeNull();
        seen!.Headers.Authorization.Should().BeNull();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
