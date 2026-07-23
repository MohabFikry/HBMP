using System.Buffers.Binary;
using System.Net.Sockets;
using Mersal.Document.Domain;
using Microsoft.Extensions.Options;

namespace Mersal.Document.Infrastructure;

public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAV";
    public string Host { get; set; } = "clamav";
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Scans an upload against ClamAV's <c>clamd</c> using the INSTREAM command over TCP (US-002).
/// Fail-closed: a positive quarantines/rejects; a scanner error is treated as unsafe (not clean).
/// </summary>
public sealed class ClamAvScanner(IOptions<ClamAvOptions> options) : IMalwareScanner
{
    private readonly ClamAvOptions _opt = options.Value;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        await client.ConnectAsync(_opt.Host, _opt.Port, timeout.Token);
        await using var net = client.GetStream();

        // INSTREAM protocol: "zINSTREAM\0" then <4-byte big-endian length><chunk>… then a 0-length terminator.
        await net.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

        var buffer = new byte[8192];
        var lenPrefix = new byte[4];
        int read;
        if (content.CanSeek) content.Position = 0;
        while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(lenPrefix, read);
            await net.WriteAsync(lenPrefix, timeout.Token);
            await net.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        BinaryPrimitives.WriteInt32BigEndian(lenPrefix, 0); // terminator
        await net.WriteAsync(lenPrefix, timeout.Token);
        await net.FlushAsync(timeout.Token);

        var respBuf = new byte[512];
        var n = await net.ReadAsync(respBuf, timeout.Token);
        var response = System.Text.Encoding.ASCII.GetString(respBuf, 0, n).Trim('\0', '\n', ' ');

        // "stream: OK" = clean; "stream: <sig> FOUND" = infected; anything else = error → treat unsafe.
        if (response.EndsWith("OK", StringComparison.Ordinal)) return ScanResult.Clean;
        if (response.EndsWith("FOUND", StringComparison.Ordinal))
        {
            var sig = response.Replace("stream:", "", StringComparison.Ordinal).Replace("FOUND", "", StringComparison.Ordinal).Trim();
            return ScanResult.Infected(sig);
        }
        return ScanResult.Infected($"scanner-error: {response}"); // fail closed
    }
}
