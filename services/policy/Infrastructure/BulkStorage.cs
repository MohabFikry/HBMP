using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.5b — the seam onto document-service's operational-document store.
///
/// <para>Three kinds of file pass through here and every one of them is treated as PHI-bearing: the uploaded
/// source file, the row-error report (which quotes member numbers and identifiers), and an extract. None of
/// them is ever written to a log, returned inline, or handed out as a durable URL — they are stored behind the
/// same fail-closed ClamAV scan every other upload passes, and read back through an authorized, audited
/// endpoint.</para>
/// </summary>
public interface IOperationalDocumentStore
{
    Task<Guid?> StoreAsync(
        string kind, Guid ownerRef, string fileName, string contentType, byte[] bytes,
        string? bearerToken, CancellationToken ct = default);
}

public sealed class HttpOperationalDocumentStore(HttpClient http) : IOperationalDocumentStore
{
    public async Task<Guid?> StoreAsync(
        string kind, Guid ownerRef, string fileName, string contentType, byte[] bytes,
        string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(file, "file", fileName);

            var url = $"/api/v1/operational-documents?kind={Uri.EscapeDataString(kind)}" +
                      $"&ownerRef={ownerRef}&ownerService=policy-service&classification=PHI";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? bearerToken["Bearer ".Length..] : bearerToken;
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await http.SendAsync(req, ct);
            // 422 is document-service's malware verdict. The caller distinguishes it from a transport failure
            // by the exception type below — a quarantine is the system working, not an outage.
            if ((int)resp.StatusCode == 422) throw new BulkFileInfectedException(await resp.Content.ReadAsStringAsync(ct));
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("documentId", out var id) && id.TryGetGuid(out var guid)
                ? guid : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Thrown when document-service quarantines the file. An exception rather than a return value because
/// there is no version of "carry on" that is correct: the job stops at Scanning and nothing is parsed.</summary>
public sealed class BulkFileInfectedException : Exception
{
    public BulkFileInfectedException(string signature)
        : base($"quarantined: {signature}") => Signature = signature;

    public BulkFileInfectedException() : this("unknown") { }

    public BulkFileInfectedException(string signature, Exception innerException)
        : base($"quarantined: {signature}", innerException) => Signature = signature;

    public string Signature { get; } = "unknown";
}

/// <summary>Reading and writing the small jsonb blobs a row carries: the normalized values, the before
/// snapshot, and the coded exclusion list.</summary>
public static class BulkSnapshots
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Write(object? value) => value is null ? "{}" : JsonSerializer.Serialize(value, Json);

    public static IReadOnlyDictionary<string, object?>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pull a contact snapshot back out of a before-blob. Returns null rather than a partly-filled
    /// contact: restoring half of somebody's phone number is worse than declining to restore it.</summary>
    public static ContactSnapshot? ReadContact(object? raw)
    {
        if (raw is null) return null;
        try
        {
            var json = raw as string ?? JsonSerializer.Serialize(raw, Json);
            var snapshot = JsonSerializer.Deserialize<ContactSnapshot>(json, Json);
            return snapshot is null || string.IsNullOrWhiteSpace(snapshot.Value) ? null : snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Semicolon-separated exclusion codes → the jsonb array benefit_rule stores.</summary>
    public static string ExclusionsJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "[]";
        var codes = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return JsonSerializer.Serialize(codes, Json);
    }
}

/// <summary>
/// The CSV writer both the error report and the extracts use.
///
/// <para>Shared for one reason: the formula-injection guard. A member number or a rejection reason beginning
/// <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is evaluated by a spreadsheet as a formula, and both of these files
/// are opened in spreadsheets by definition. One escaper, used by every file that leaves.</para>
/// </summary>
public static class BulkCsv
{
    public static byte[] Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', headers.Select(Escape)));
        foreach (var row in rows) csv.AppendLine(string.Join(',', row.Select(Escape)));
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return v.Contains(',', StringComparison.Ordinal)
               || v.Contains('"', StringComparison.Ordinal)
               || v.Contains('\n', StringComparison.Ordinal)
            ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : v;
    }

    public static string Num(decimal? d) => d?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";

    public static string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
}
