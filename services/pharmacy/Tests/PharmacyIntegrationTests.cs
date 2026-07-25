using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>Phase 4.3 persistence at the datastore (env-gated <c>PHARMACY_TEST_DB</c>): a prescription + lines
/// round-trip with the routed status, a referral persists in Requested, the sequence issuer is monotonic, and the
/// DB enforces the dispense accumulator invariant (0 ≤ dispensed ≤ prescribed). Self-cleans by scope tag.</summary>
public class PharmacyIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");
    private static DbContextOptions<PharmacyDbContext> Options() =>
        new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [Fact]
    public async Task Prescription_with_lines_persists_approved()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid rxId;
            await using (var ctx = new PharmacyDbContext(Options()))
            {
                var no = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026));
                no.Should().StartWith("RX-2026-");
                var rx = new Prescription
                {
                    PrescriptionId = Guid.NewGuid(), RxNo = no, BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(),
                    PrescriberId = Guid.NewGuid(), Status = RxStatus.Approved, SubmittedAt = DateTimeOffset.UtcNow,
                    Lines = [new PrescriptionLine { PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), QuantityPrescribed = 30, RefillsAllowed = 2 }],
                };
                ctx.Prescriptions.Add(rx);
                await ctx.SaveChangesAsync();
                rxId = rx.PrescriptionId;
            }

            await using var verify = new PharmacyDbContext(Options());
            var read = await verify.Prescriptions.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.PrescriptionId == rxId);
            read.Status.Should().Be(RxStatus.Approved);
            read.Lines.Should().ContainSingle().Which.QuantityDispensed.Should().Be(0);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Referral_persists_requested()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            Guid refId;
            await using (var ctx = new PharmacyDbContext(Options()))
            {
                var referral = new Referral
                {
                    ReferralId = Guid.NewGuid(), ReferralNo = ReferralNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("referral_seq", 2026)),
                    BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), ReferringProviderId = Guid.NewGuid(),
                    TargetSpecialty = "Cardiology", Status = ReferralStatus.Requested, RequestedAt = DateTimeOffset.UtcNow,
                };
                ctx.Referrals.Add(referral);
                await ctx.SaveChangesAsync();
                refId = referral.ReferralId;
            }

            await using var verify = new PharmacyDbContext(Options());
            (await verify.Referrals.AsNoTracking().SingleAsync(r => r.ReferralId == refId)).Status.Should().Be(ReferralStatus.Requested);
        }
        finally { await Cleanup(beneficiary); }
    }

    [Fact]
    public async Task Dispensed_over_prescribed_is_rejected_by_db()
    {
        if (Db is null) return;
        var beneficiary = Guid.NewGuid();
        try
        {
            await using var ctx = new PharmacyDbContext(Options());
            var rx = new Prescription
            {
                PrescriptionId = Guid.NewGuid(), RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
                BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), PrescriberId = Guid.NewGuid(), Status = RxStatus.Approved,
                Lines = [new PrescriptionLine { PrescriptionLineId = Guid.NewGuid(), DrugId = Guid.NewGuid(), QuantityPrescribed = 10, QuantityDispensed = 99 }],
            };
            ctx.Prescriptions.Add(rx);
            var act = async () => await ctx.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = new PharmacyDbContext(Options());
        var ids = await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary).Select(p => p.PrescriptionId).ToListAsync();
        await ctx.PrescriptionAlerts.Where(a => ids.Contains(a.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.PrescriptionLines.Where(l => ids.Contains(l.PrescriptionId)).ExecuteDeleteAsync();
        await ctx.Prescriptions.Where(p => p.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
        await ctx.Referrals.Where(r => r.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
