using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Approvals.Infrastructure;

namespace Mersal.Approvals.Api;

/// <summary>Assembles the reviewer's field-scoped clinical context by calling emr-service's oversight projection
/// with the caller's bearer token (so emr enforces its own <c>emr:read-oversight</c> rule — defense in depth) and
/// document-service for supporting reports. It returns ONLY the minimum-necessary projection, never raw records.
/// Fail-closed: if the oversight projection cannot be assembled it returns <c>null</c> (the review view then shows
/// "clinical context unavailable" rather than fabricating PHI). The emr oversight endpoint is the integration seam;
/// until emr exposes it this degrades to null, and the reviewer sees the request header without clinical detail.</summary>
public sealed class HttpClinicalContextClient(HttpClient http) : IClinicalContextProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ClinicalContext?> GetAsync(Guid beneficiaryId, string? sourceRef, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/beneficiaries/{beneficiaryId}/clinical-context");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearerToken["Bearer ".Length..] : bearerToken;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<ClinicalContextDto>(stream, Json, ct);
            if (dto is null) return null;

            return new ClinicalContext(
                dto.EmrSummary ?? "",
                (dto.Notes ?? []).Select(n => new ClinicalNote(n.Type ?? "", n.Author ?? "", n.AuthoredAt, n.Summary ?? "")).ToList(),
                (dto.Documents ?? []).Select(d => new SupportingDocument(d.DocumentId, d.Kind ?? "", d.FileName ?? "")).ToList());
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private sealed record ClinicalContextDto(string? EmrSummary, List<NoteDto>? Notes, List<DocDto>? Documents);
    private sealed record NoteDto(string? Type, string? Author, DateTimeOffset AuthoredAt, string? Summary);
    private sealed record DocDto(Guid DocumentId, string? Kind, string? FileName);
}
