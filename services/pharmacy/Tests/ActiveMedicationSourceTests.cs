using FluentAssertions;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 32.1 — the WIRING of the active-medication list, against real rows.
/// </summary>
/// <remarks>
/// <para>
/// The engine's side of this is proven in <c>Mersal.ClinicalValidation.Tests</c>, and was proven there while
/// the feature did not work: <c>A_line_is_checked_against_medications_the_patient_already_takes</c> handed
/// the loop a populated list and watched it behave, for months, while both production call sites handed it
/// an empty one. A unit test can only prove the code it is handed.
/// </para>
/// <para>
/// So these tests do not construct a list. They put PRESCRIPTIONS in a database and ask the source what the
/// patient is taking — which is the question that was never being asked.
/// </para>
/// </remarks>
[Collection("pharmacy-db")]
public class ActiveMedicationSourceTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("PHARMACY_TEST_DB");

    /// <summary>
    /// The tenant every seeded row belongs to.
    /// </summary>
    /// <remarks>
    /// Stamped explicitly because this fixture builds rows through a PLAIN DbContext, with none of the
    /// interceptors the API composes: production is stamped by TenantStampingInterceptor from the bound
    /// request, and a fixture that skips it writes rows belonging to NO tenant — invisible to every real one
    /// and visible to any session binding an empty one. The tenant-isolation fuzzer caught exactly that here,
    /// which is the control working: this suite had left 60 unscoped prescriptions in the dev database.
    /// </remarks>
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PharmacyDbContext Ctx() =>
        new(new DbContextOptionsBuilder<PharmacyDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    [SkippableFact]
    public async Task An_active_line_on_a_live_prescription_is_a_current_medication()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        var drug = await SeedLine(beneficiary, "Warfarin 5mg", RxStatus.Approved, RxLineStatus.Active);

        await using var ctx = Ctx();
        var items = await new DbPrescribedMedicationSource(ctx).ActiveForAsync(beneficiary, DateTimeOffset.UtcNow);

        items.Should().ContainSingle(m => m.DrugId == drug && m.DrugName == "Warfarin 5mg" && m.Source == "Prescribed");
    }

    [SkippableFact]
    public async Task A_dispensed_line_is_still_a_current_medication()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        // Collected is the STRONGEST evidence the platform has that the patient is on something. Excluding
        // dispensed lines would drop precisely the medicines most certainly in their hands.
        var beneficiary = Guid.NewGuid();
        var drug = await SeedLine(beneficiary, "Metformin 850mg", RxStatus.Dispensed, RxLineStatus.Dispensed);

        await using var ctx = Ctx();
        var items = await new DbPrescribedMedicationSource(ctx).ActiveForAsync(beneficiary, DateTimeOffset.UtcNow);

        items.Should().ContainSingle(m => m.DrugId == drug);
    }

    [SkippableFact]
    public async Task A_cancelled_line_a_superseded_line_and_a_draft_script_are_not()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        // Cancelled and Superseded lines must carry their attribution — pharmacy 0013's
        // ck_rx_line_amendment_attributed refuses a row that stepped aside without recording who did it and
        // why. The fixture obeys the schema rather than working around it: a test that seeded an
        // unattributed superseded line would be exercising a row the platform cannot produce.
        var cancelled = await SeedLine(beneficiary, "Stopped drug", RxStatus.Approved, RxLineStatus.Cancelled,
            attributed: true);
        var (superseded, successor) = await SeedSupersededPair(beneficiary);
        var draft = await SeedLine(beneficiary, "Never submitted", RxStatus.Draft, RxLineStatus.Active);

        await using var ctx = Ctx();
        var items = await new DbPrescribedMedicationSource(ctx).ActiveForAsync(beneficiary, DateTimeOffset.UtcNow);

        items.Should().NotContain(m => m.DrugId == cancelled);
        items.Should().NotContain(m => m.DrugId == superseded);
        items.Should().Contain(m => m.DrugId == successor,
            "an amended line steps aside FOR a successor, and the successor is what the patient now takes");
        items.Should().NotContain(m => m.DrugId == draft, "a draft is not something the patient is taking");
    }

    [SkippableFact]
    public async Task An_expired_prescription_is_not_a_current_medication()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        var drug = await SeedLine(beneficiary, "Finished course", RxStatus.Approved, RxLineStatus.Active,
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        await using var ctx = Ctx();
        var items = await new DbPrescribedMedicationSource(ctx).ActiveForAsync(beneficiary, DateTimeOffset.UtcNow);

        items.Should().NotContain(m => m.DrugId == drug);
    }

    [SkippableFact]
    public async Task Another_beneficiarys_medication_is_never_returned()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var hers = await SeedLine(theirs, "Someone else's medicine", RxStatus.Approved, RxLineStatus.Active);

        await using var ctx = Ctx();
        var items = await new DbPrescribedMedicationSource(ctx).ActiveForAsync(mine, DateTimeOffset.UtcNow);

        items.Should().NotContain(m => m.DrugId == hers);
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// An amended line and the line it stepped aside for, as the amendment path really writes them.
    /// </summary>
    /// <remarks>
    /// Two database constraints make the shortcut impossible, and both are right:
    /// <c>ck_rx_line_amendment_attributed</c> refuses a superseded line that does not record who did it and
    /// why, and <c>ck_rx_line_superseded_has_successor</c> refuses one that does not point at a real
    /// successor row (there is a foreign key). A fixture that faked either would be testing a row the
    /// platform cannot produce.
    /// </remarks>
    private static async Task<(Guid Superseded, Guid Successor)> SeedSupersededPair(Guid beneficiary)
    {
        await using var ctx = Ctx();
        var successorDrug = Guid.NewGuid();
        var supersededDrug = Guid.NewGuid();

        var successor = new PrescriptionLine
        {
            PrescriptionLineId = Guid.NewGuid(), TenantId = Tenant,
            DrugId = successorDrug, DrugName = "Amlodipine 10mg",
            Dose = "10mg", Route = "PO", Frequency = "OD", QuantityPrescribed = 30,
            Status = RxLineStatus.Active,
        };
        var original = new PrescriptionLine
        {
            PrescriptionLineId = Guid.NewGuid(), TenantId = Tenant,
            DrugId = supersededDrug, DrugName = "Amlodipine 5mg",
            Dose = "5mg", Route = "PO", Frequency = "OD", QuantityPrescribed = 30,
            Status = RxLineStatus.Superseded, SupersededById = successor.PrescriptionLineId,
            AmendmentReasonCode = "ClinicalChange", AmendedBy = Guid.NewGuid(), AmendedAt = DateTimeOffset.UtcNow,
        };

        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(), TenantId = Tenant,
            RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), PrescriberId = Guid.NewGuid(),
            Status = RxStatus.Approved, SubmittedAt = DateTimeOffset.UtcNow,
            Lines = [successor],
        };
        ctx.Prescriptions.Add(rx);
        await ctx.SaveChangesAsync();

        // The successor lands FIRST, in its own round trip. superseded_by_id carries a foreign key to
        // prescription_line, and it is a plain scalar in the model rather than a mapped navigation — so EF
        // has no dependency to order the batch by and will happily write the referring row first.
        original.PrescriptionId = rx.PrescriptionId;
        original.TenantId = rx.TenantId;
        ctx.PrescriptionLines.Add(original);
        await ctx.SaveChangesAsync();

        return (supersededDrug, successorDrug);
    }

    private static async Task<Guid> SeedLine(
        Guid beneficiary, string drugName, RxStatus rxStatus, RxLineStatus lineStatus,
        DateTimeOffset? expiresAt = null, bool attributed = false)
    {
        await using var ctx = Ctx();
        var drugId = Guid.NewGuid();
        var rx = new Prescription
        {
            PrescriptionId = Guid.NewGuid(), TenantId = Tenant,
            RxNo = RxNo.Format(2026, await new SequenceIssuer(ctx).NextAsync("rx_seq", 2026)),
            BeneficiaryId = beneficiary, EncounterId = Guid.NewGuid(), PrescriberId = Guid.NewGuid(),
            Status = rxStatus, SubmittedAt = DateTimeOffset.UtcNow, ExpiresAt = expiresAt,
            Lines =
            [
                new PrescriptionLine
                {
                    PrescriptionLineId = Guid.NewGuid(), TenantId = Tenant,
                    DrugId = drugId, DrugName = drugName,
                    Dose = "1", Route = "PO", Frequency = "OD",
                    QuantityPrescribed = 30, Status = lineStatus,
                    AmendmentReasonCode = attributed ? "ClinicalChange" : null,
                    AmendedBy = attributed ? Guid.NewGuid() : null,
                    AmendedAt = attributed ? DateTimeOffset.UtcNow : null,
                },
            ],
        };
        ctx.Prescriptions.Add(rx);
        await ctx.SaveChangesAsync();
        return drugId;
    }
}
