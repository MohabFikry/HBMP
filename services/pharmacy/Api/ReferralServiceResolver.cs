using System.Net;
using System.Text.Json;
using Mersal.Auth;

namespace Mersal.Pharmacy.Api;

/// <summary>
/// 29.2 — what pharmacy learns about a CPT code before raising a referral for it (design 45 §2).
///
/// <para><b>Resolved by MASTERDATA, not computed here.</b> The section — and therefore the vehicle — is a
/// pure function of the code, but the range table that defines it belongs to masterdata's
/// <c>CptSections</c>. A second copy in pharmacy is how the two services come to disagree about where
/// Medicine ends and E/M begins, and that disagreement is precisely what turns a referral into a procedure
/// order nobody closes the loop on. Orders-service resolves the same fact the same way and for the same
/// reason — see <c>ProcedureTypeResolver</c>.</para>
/// </summary>
/// <param name="Vehicle">
/// <c>Referral</c>, <c>ProcedureOrder</c>, <c>LabOrder</c>, <c>RadiologyOrder</c> — or null when the code is
/// unknown or masterdata could not be reached. Null is FAIL-CLOSED at the caller: "we could not find out"
/// is never read as "any vehicle is fine".
/// </param>
public sealed record ReferralServiceLookup(string? Vehicle, string? Section);

public interface IReferralServiceResolver
{
    Task<ReferralServiceLookup> ResolveAsync(string? cptCode, string? bearer, CancellationToken ct = default);
}

/// <summary>HTTP resolver against masterdata-service, bearer-forwarded.</summary>
public sealed class HttpReferralServiceResolver(IHttpClientFactory factory) : IReferralServiceResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReferralServiceLookup> ResolveAsync(
        string? cptCode, string? bearer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cptCode)) return new ReferralServiceLookup(null, null);

        var http = factory.CreateClient("masterdata");
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cpt-codes/{Uri.EscapeDataString(cptCode)}/section");
        BearerHeader.Apply(req, bearer);

        using var resp = await http.SendAsync(req, ct);
        // 404 — the code is not in the catalogue at all. Fail-closed, same as unreachable: a referral raised
        // for a code masterdata has never heard of names a service nobody can report against.
        if (resp.StatusCode == HttpStatusCode.NotFound) return new ReferralServiceLookup(null, null);
        resp.EnsureSuccessStatusCode();

        var row = await resp.Content.ReadFromJsonAsync<SectionRow>(Json, ct);
        return new ReferralServiceLookup(row?.Vehicle, row?.Section);
    }

    private sealed record SectionRow(string Code, string Section, string Vehicle, bool Orderable);
}
