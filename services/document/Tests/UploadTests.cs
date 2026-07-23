using System.Text;
using FluentAssertions;
using Mersal.Document.Domain;

namespace Mersal.Document.Tests;

public class UploadValidatorTests
{
    private readonly UploadValidator _v = new();

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("application/x-msdownload", false)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void Only_allowed_mime_types_pass(string? mime, bool ok)
        => _v.Validate(mime, 1024).IsValid.Should().Be(ok);

    [Fact]
    public void Oversize_is_rejected_with_reason()
    {
        var r = _v.Validate("application/pdf", 20 * 1024 * 1024);
        r.IsValid.Should().BeFalse();
        r.Reason.Should().Contain("exceeds max");
    }

    [Fact]
    public void Empty_file_is_rejected()
        => _v.Validate("application/pdf", 0).IsValid.Should().BeFalse();
}

public class UploadPipelineTests
{
    private sealed class FakeScanner(ScanResult result) : IMalwareScanner
    {
        public int Calls { get; private set; }
        public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default) { Calls++; return Task.FromResult(result); }
    }
    private sealed class FakeBlobStore : IBlobStore
    {
        public int Puts { get; private set; }
        public Task<string> PutAsync(string container, string key, Stream content, string contentType, CancellationToken ct = default)
        { Puts++; return Task.FromResult($"s3://bucket/{container}/{key}"); }
    }

    private static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4 fake pdf bytes");

    private static DocumentUploadService Service(IMalwareScanner scanner, IBlobStore blobs)
        => new(new UploadValidator(), scanner, blobs, TimeProvider.System);

    [Fact]
    public async Task Clean_file_is_stored_with_version_checksum_and_uploader()
    {
        var scanner = new FakeScanner(ScanResult.Clean);
        var blobs = new FakeBlobStore();
        var outcome = await Service(scanner, blobs).UploadAsync(
            DocType.IDScan, Guid.NewGuid(), Classification.PHI, "application/pdf", Pdf(), "officer-1");

        var stored = outcome.Should().BeOfType<UploadOutcome.Stored>().Subject;
        stored.Version.VersionNo.Should().Be(1);
        stored.Version.ChecksumSha256.Should().HaveLength(64);
        stored.Version.UploadedBy.Should().Be("officer-1");
        stored.Version.SizeBytes.Should().Be(Pdf().Length);
        blobs.Puts.Should().Be(1);       // only clean files reach the blob store
        scanner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Malware_positive_is_quarantined_and_never_stored()
    {
        var blobs = new FakeBlobStore();
        var outcome = await Service(new FakeScanner(ScanResult.Infected("Eicar-Test-Signature")), blobs)
            .UploadAsync(DocType.IDScan, Guid.NewGuid(), Classification.PHI, "application/pdf", Pdf(), "officer-1");

        outcome.Should().BeOfType<UploadOutcome.Quarantined>()
            .Which.Signature.Should().Be("Eicar-Test-Signature");
        blobs.Puts.Should().Be(0);       // nothing stored on a positive (fail-closed)
    }

    [Fact]
    public async Task Disallowed_type_is_rejected_before_scan_or_store()
    {
        var scanner = new FakeScanner(ScanResult.Clean);
        var blobs = new FakeBlobStore();
        var outcome = await Service(scanner, blobs)
            .UploadAsync(DocType.IDScan, Guid.NewGuid(), Classification.PHI, "application/x-msdownload", Pdf(), "o");

        outcome.Should().BeOfType<UploadOutcome.Rejected>();
        scanner.Calls.Should().Be(0);    // rejected before scanning
        blobs.Puts.Should().Be(0);
    }

    [Fact]
    public async Task Second_upload_versions_the_existing_document()
    {
        var scanner = new FakeScanner(ScanResult.Clean);
        var blobs = new FakeBlobStore();
        var svc = Service(scanner, blobs);
        var owner = Guid.NewGuid();
        var first = (UploadOutcome.Stored)await svc.UploadAsync(DocType.Consent, owner, Classification.Internal, "image/png", Pdf(), "o");

        var second = await svc.UploadAsync(DocType.Consent, owner, Classification.Internal, "image/png", Pdf(), "o", existing: first.Document);

        second.Should().BeOfType<UploadOutcome.Stored>().Which.Version.VersionNo.Should().Be(2);
    }
}
