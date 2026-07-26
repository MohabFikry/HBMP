using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Orders.Domain;

namespace Mersal.Orders.Api;

/// <summary>The pinned classification resolved from masterdata for an examination type (phase 14.6).</summary>
public sealed record ExaminationClassification(SensitivityLevel SensitivityLevel, string? SensitiveCategory);

/// <summary>Resolves + pins an examination type's sensitivity at order creation (design 37 §5). FAIL-CLOSED:
/// an unknown examination_type_id returns null → the caller rejects the order 422.</summary>
public interface IExaminationTypeResolver
{
    Task<ExaminationClassification?> ResolveAsync(Guid examinationTypeId, string? bearer, CancellationToken ct = default);
}

/// <summary>HTTP resolver against masterdata-service (<c>GET /examination-types/{id}</c>), bearer-forwarded.</summary>
public sealed class HttpExaminationTypeResolver(HttpClient http) : IExaminationTypeResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ExaminationClassification?> ResolveAsync(Guid examinationTypeId, string? bearer, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/examination-types/{examinationTypeId}");
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // fail-closed
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<ExamDto>(Json, ct);
        return dto is null || !Enum.TryParse<SensitivityLevel>(dto.SensitivityLevel, out var lvl)
            ? null
            : new ExaminationClassification(lvl, dto.SensitiveCategory);
    }

    private sealed record ExamDto(Guid ExaminationTypeId, string SensitivityLevel, string? SensitiveCategory);
}
