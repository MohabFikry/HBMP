using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.Orders.Infrastructure;

namespace Mersal.Orders.Api;

/// <summary>Stores a result report by POSTing it to document-service (which checksums, malware-scans fail-closed,
/// and stores the clean blob under CMK), forwarding the caller's bearer token. Returns the created document id to
/// pin on the fulfillment row, or null if the upload was rejected/quarantined/unreachable (fail-closed: no ref).</summary>
public sealed class HttpReportDocumentClient(HttpClient http) : IReportDocumentClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid?> StoreReportAsync(
        Guid beneficiaryId, string fileName, string contentType, byte[] content, string? bearerToken, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "result-report" : fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/beneficiaries/{beneficiaryId}/documents?docType=LabResult&classification=PHI") { Content = form };
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;   // rejected / quarantined / unreachable → no blob ref
        var body = await resp.Content.ReadFromJsonAsync<DocDto>(Json, ct);
        return body?.DocumentId;
    }

    private sealed record DocDto(Guid DocumentId);
}
