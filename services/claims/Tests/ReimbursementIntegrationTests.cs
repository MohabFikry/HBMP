using System.Reflection;
using FluentAssertions;
using Mersal.Claims.Api;
using Mersal.Claims.Domain;
using Mersal.Claims.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Tests;

/// <summary>Beneficiary reimbursement + OCR at the datastore (env-gated <c>CLAIMS_TEST_DB</c>). Proves: OCR extractions
/// are persisted append-only with confidence + region (and are immutable); a second OCR provider is used with no code
/// change (swappability); a high-confidence unambiguous match flags AutoMatched yet stays unpayable until a human
/// confirms AND an officer decides; low confidence routes to ManualAssessment; a malware-scan failure is rejected with
/// nothing stored; and the claims schema carries no bank/payout field. Self-cleans by tenant scope.</summary>
[Collection("claims-db")]
public class ReimbursementIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CLAIMS_TEST_DB");
    private static DbContextOptions<ClaimsDbContext> Options() =>
        new DbContextOptionsBuilder<ClaimsDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;
    private static ClaimsDbContext Ctx() => new(Options());

    // ---- seams -------------------------------------------------------------------------------------------
    private sealed class FakeOcr(string engine, params OcrField[] fields) : IDocumentOcrProvider
    {
        public string Engine => engine;
        public string EngineVersion => "1.0";
        public Task<IReadOnlyList<OcrField>> ExtractAsync(Guid d, string langs, string? b, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OcrField>>(fields);
    }
    private sealed class FakeScanner(bool clean) : IDocumentScanner
    {
        public Task<ScanResult> ScanAsync(Guid d, string? b, CancellationToken ct = default)
            => Task.FromResult(new ScanResult(clean, clean ? null : "EICAR-TEST"));
    }
    private sealed class FakeAuthz(params AuthorizedService[] svcs) : IAuthorizedServiceResolver
    {
        public Task<IReadOnlyList<AuthorizedService>> ResolveAsync(Guid b, Guid? o, Guid? p, string? bearer, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuthorizedService>>(svcs);
    }
    private sealed class FakeTariff(decimal? price) : IContractTariffProvider
    {
        public Task<decimal?> ResolveAsync(Guid pr, ClaimCodeSystem cs, string code, DateOnly d, string? b, CancellationToken ct = default)
            => Task.FromResult(price);
    }

    private static ReimbursementService Svc(
        ClaimsDbContext db, IDocumentOcrProvider ocr, IDocumentScanner scanner, IAuthorizedServiceResolver authz,
        decimal? tariff = 180m, decimal threshold = 0.90m) =>
        new(db, new ClaimNoIssuer(db), ocr, scanner, authz, new FakeTariff(tariff), TimeProvider.System,
            new ReimbursementOptions { Languages = "ara+eng", ConfidenceThreshold = threshold });

    private static ReimbursementSubmission Sub(Guid beneficiary, Guid? orderId, decimal receipt = 200m) => new(
        beneficiary, null, receipt, "EGP", orderId, null,
        [new ReimbursementDoc(Guid.NewGuid(), ClaimDocType.Receipt, "application/pdf", 2048),
         new ReimbursementDoc(Guid.NewGuid(), ClaimDocType.ResultProof, "image/png", 4096)]);

    private static OcrField[] HighConfFields() =>
    [
        new(OcrFields.Provider, "Cairo Lab", 0.97m, 1, "{\"x\":1,\"y\":2,\"w\":3,\"h\":4}"),
        new(OcrFields.Amount, "200.00", 0.95m, 1, "{\"x\":5}"),
        new(OcrFields.ServiceDate, "2026-07-10", 0.93m, 1, null),
    ];

    [Fact]
    public async Task Ocr_extractions_are_persisted_appendonly_with_confidence_and_region()
    {
        if (Db is null) return;
        var tenant = T();
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var r = await Svc(db, new FakeOcr("tesseract", HighConfFields()), new FakeScanner(true), new FakeAuthz())
                .SubmitAsync(tenant, "member", Sub(beneficiary, null), null);
            r.Outcome.Should().Be(ReimbursementOutcome.ManualAssessment, "no authorized service resolved");

            await using var verify = Ctx();
            var rows = await verify.OcrExtractions.AsNoTracking().Where(x => x.RequestId == r.Request!.RequestId).ToListAsync();
            rows.Should().HaveCount(3);
            rows.Should().OnlyContain(x => x.Confidence > 0 && x.Engine == "tesseract");
            rows.Should().Contain(x => x.Region != null);

            // append-only: an attempt to CHANGE an extracted value is rejected by the trigger.
            var id = rows[0].ExtractionId;
            var act = async () => await verify.Database.ExecuteSqlRawAsync(
                "UPDATE claims.ocr_extraction SET extracted_value = 'tamper' WHERE extraction_id = {0}", id);
            await act.Should().ThrowAsync<Exception>();
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task A_second_ocr_provider_is_used_with_no_code_change()
    {
        if (Db is null) return;
        var tenant = T();
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            // Swap the engine implementation only — the service is unchanged.
            var r = await Svc(db, new FakeOcr("azure-docintel", HighConfFields()), new FakeScanner(true), new FakeAuthz())
                .SubmitAsync(tenant, "member", Sub(beneficiary, null), null);
            (await Ctx().OcrExtractions.AsNoTracking().Where(x => x.RequestId == r.Request!.RequestId).ToListAsync())
                .Should().OnlyContain(x => x.Engine == "azure-docintel");
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task High_confidence_unambiguous_match_is_auto_matched_but_stays_unpayable_until_a_human_decides()
    {
        if (Db is null) return;
        var tenant = T();
        var (beneficiary, orderId, provider) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var authz = new FakeAuthz(new AuthorizedService(orderId, null, provider, new DateOnly(2026, 7, 10), ClaimCodeSystem.CPT, "80053", 180m));
            Guid requestId;
            await using (var db = Ctx())
            {
                var r = await Svc(db, new FakeOcr("tesseract", HighConfFields()), new FakeScanner(true), authz)
                    .SubmitAsync(tenant, "member", Sub(beneficiary, orderId), null);
                r.Outcome.Should().Be(ReimbursementOutcome.AutoMatched);
                r.Request!.Status.Should().Be(ReimbursementStatus.AutoMatched);
                r.Request.MatchMethod.Should().Be(ReimbursementMatchMethod.AutoOcr);
                requestId = r.Request.RequestId;
                // No claim exists yet — AutoMatched is assistive, not a payment.
                (await Ctx().ReimbursementRequests.AsNoTracking().SingleAsync(x => x.RequestId == requestId)).ClaimId.Should().BeNull();
            }

            // Human confirm creates the claim with a PENDING line — still not payable.
            await using (var db = Ctx())
            {
                var c = await Svc(db, new FakeOcr("tesseract"), new FakeScanner(true), authz)
                    .ConfirmAsync(requestId, tenant, "officer", null, null, null);
                c.Outcome.Should().Be(ConfirmOutcome.Created);
                c.Claim!.Lines.Single().Status.Should().Be(ClaimLineStatus.Pending);
            }
            await using var verify = Ctx();
            var req = await verify.ReimbursementRequests.AsNoTracking().SingleAsync(x => x.RequestId == requestId);
            req.Status.Should().Be(ReimbursementStatus.Adjudicating);
            var claim = await verify.Claims.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.ClaimId == req.ClaimId);
            claim.Origin.Should().Be(ClaimOrigin.Reimbursement);
            claim.Lines.Should().OnlyContain(l => l.Status == ClaimLineStatus.Pending, "no reimbursement line is payable without an officer decision");
            // The OCR values were accepted by the human at confirm.
            (await verify.OcrExtractions.AsNoTracking().Where(x => x.RequestId == requestId).ToListAsync())
                .Should().OnlyContain(x => x.AcceptedBy == "officer");
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Low_confidence_routes_to_manual_assessment()
    {
        if (Db is null) return;
        var tenant = T();
        var (beneficiary, orderId, provider) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var authz = new FakeAuthz(new AuthorizedService(orderId, null, provider, new DateOnly(2026, 7, 10), ClaimCodeSystem.CPT, "80053", 180m));
            var lowConf = new OcrField(OcrFields.Amount, "200", 0.40m, 1, null);
            await using var db = Ctx();
            var r = await Svc(db, new FakeOcr("tesseract", lowConf), new FakeScanner(true), authz)
                .SubmitAsync(tenant, "member", Sub(beneficiary, orderId), null);
            r.Outcome.Should().Be(ReimbursementOutcome.ManualAssessment);
            r.Request!.Status.Should().Be(ReimbursementStatus.ManualAssessment);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task A_malware_scan_failure_is_rejected_with_nothing_stored()
    {
        if (Db is null) return;
        var tenant = T();
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var db = Ctx();
            var r = await Svc(db, new FakeOcr("tesseract", HighConfFields()), new FakeScanner(clean: false), new FakeAuthz())
                .SubmitAsync(tenant, "member", Sub(beneficiary, null), null);
            r.Outcome.Should().Be(ReimbursementOutcome.RejectedScan);
            r.Request.Should().BeNull();
            (await Ctx().ReimbursementRequests.CountAsync(x => x.TenantId == tenant)).Should().Be(0);
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public void The_claims_schema_carries_no_bank_or_payout_field()
    {
        string[] forbidden = ["bank", "iban", "account", "payout", "swift", "cardnumber"];
        foreach (var t in new[] { typeof(ReimbursementRequest), typeof(OcrExtraction),
                                  typeof(ReimbursementView), typeof(OcrFieldView) })
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            props.Should().NotContain(p => forbidden.Any(p.Contains), $"{t.Name} must store no bank/payout detail");
        }
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        if (Db is null) return;
        await using var db = Ctx();
        // ocr_extraction is append-only (trigger blocks DELETE); disable user triggers for this cleanup only.
        await db.Database.ExecuteSqlRawAsync(
            "SET session_replication_role = replica; " +
            "DELETE FROM claims.ocr_extraction WHERE request_id IN (SELECT request_id FROM claims.reimbursement_request WHERE tenant_id = {0}); " +
            "DELETE FROM claims.reimbursement_request WHERE tenant_id = {0}; " +
            "DELETE FROM claims.claim_line WHERE claim_id IN (SELECT claim_id FROM claims.claim WHERE tenant_id = {0}); " +
            "DELETE FROM claims.claim WHERE tenant_id = {0}; " +
            "SET session_replication_role = origin;", tenant);
    }
}
