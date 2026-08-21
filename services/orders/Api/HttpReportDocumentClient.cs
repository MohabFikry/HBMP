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

    /// <summary>
    /// Fetch a stored report's bytes from document-service, forwarding the CALLER's bearer.
    /// </summary>
    /// <remarks>
    /// The caller's token rather than a service credential, exactly as <c>StoreReportAsync</c> does: the
    /// 14.7 decision has already been made by the endpoint above, and document-service's own role and tenant
    /// rules should still apply underneath it rather than being stepped over.
    /// </remarks>
    public async Task<ReportBlob?> FetchReportAsync(
        Guid beneficiaryId, Guid documentId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/beneficiaries/{beneficiaryId}/documents/{documentId}/content");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearerToken["Bearer ".Length..] : bearerToken;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // ResponseHeadersRead so a large study is streamed on rather than buffered whole in this service.
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) { resp.Dispose(); return null; }

        var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "result-report";
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return new ReportBlob(stream, contentType, fileName);
    }

    private sealed record DocDto(Guid DocumentId);
}
