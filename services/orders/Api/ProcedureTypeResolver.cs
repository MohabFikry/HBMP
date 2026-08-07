using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>
/// 29.2 — what orders-service learns about a procedure type AND the code it was paired with (design 45 §2).
///
/// <para><see cref="Section"/> is resolved by MASTERDATA, not computed here. The CPT section is a pure
/// function of the code, but the range table that defines it belongs to masterdata's <c>CptSections</c>, and a
/// second copy in orders is how the two services come to disagree about where Medicine ends and E/M begins —
/// which is the disagreement that turns a referral into a procedure order nobody closes the loop on.</para>
/// </summary>
public sealed record ProcedureTypeLookup(string? Section, ProcedureTypeFacts? Facts);

/// <summary>
/// Resolves an OP-Procedure type and the section of the code it accompanies. FAIL-CLOSED: unknown, retired
/// or unreachable all return <c>Facts = null</c>, and the caller refuses the order 422.
///
/// <para>The composer already validated the pairing, and that verdict is DISPLAY STATE — the same reasoning
/// orders-service applies to its CPT section check. A physiotherapy type on a minor-surgery code is the same
/// shape of error as a chest x-ray on a lab order, and the only place either can actually be prevented is the
/// write path.</para>
/// </summary>
public interface IProcedureTypeResolver
{
    Task<ProcedureTypeLookup> ResolveAsync(string? typeCode, string? cptCode, string? bearer, CancellationToken ct = default);
}

/// <summary>HTTP resolver against masterdata-service, bearer-forwarded.</summary>
public sealed class HttpProcedureTypeResolver(HttpClient http) : IProcedureTypeResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ProcedureTypeLookup> ResolveAsync(
        string? typeCode, string? cptCode, string? bearer, CancellationToken ct = default)
    {
        var section = await SectionAsync(cptCode, bearer, ct);
        if (string.IsNullOrWhiteSpace(typeCode)) return new ProcedureTypeLookup(section, null);

        var rows = await GetAsync<List<Row>>("/api/v1/procedure-types", bearer, ct) ?? [];
        var row = rows.FirstOrDefault(r => string.Equals(r.Code, typeCode, StringComparison.OrdinalIgnoreCase));

        return new ProcedureTypeLookup(section, row is null
            ? null
            : new ProcedureTypeFacts(row.Code, row.IsSessionBased, row.MaxSessions, row.AllowedCptScopes ?? [], row.IsActive));
    }

    /// <summary>The code's CPT section, per masterdata. Null when the code is absent or masterdata cannot be
    /// reached — which the section check must read as "refuse", never as "any section is fine".</summary>
    private async Task<string?> SectionAsync(string? cptCode, string? bearer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cptCode)) return null;
        var r = await GetAsync<SectionRow>(
            $"/api/v1/cpt-codes/{Uri.EscapeDataString(cptCode)}/section", bearer, ct);
        return r?.Section;
    }

    private async Task<T?> GetAsync<T>(string path, string? bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return default;   // fail-closed
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private sealed record Row(
        string Code, bool IsSessionBased, int? DefaultSessions, int? MaxSessions,
        List<string>? AllowedCptScopes, bool IsActive);

    private sealed record SectionRow(string Code, string Section, string Vehicle, bool Orderable);
}
