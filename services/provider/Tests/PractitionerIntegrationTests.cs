using FluentAssertions;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Tests;

/// <summary>Phase 14.5 practitioners at the datastore (env-gated <c>PROVIDER_TEST_DB_OWNER</c>, migration 0006
/// applied). Proves the one-primary-specialty invariant (partial-unique → second primary rejected), that a
/// doctor may serve one-or-many branches and the serves-branch probe is validity-windowed, and that the
/// specialty seed includes Psychiatry + Clinical Psychology (they drive 14.6 sensitivity defaults).
/// Self-cleans by a unique tenant scope.</summary>
public class PractitionerIntegrationTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");
    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static readonly DateOnly From = new(2026, 1, 1);

    [Fact]
    public async Task Specialty_seed_includes_the_mental_health_specialties()
    {
        if (Owner is null) return;
        await using var db = Ctx();
        var codes = await db.Specialties.AsNoTracking().Select(s => s.SpecialtyCode).ToListAsync();
        codes.Should().Contain(["PSYCH", "CPSY", "GP"]);
    }

    [Fact]
    public async Task A_second_primary_specialty_is_rejected()
    {
        if (Owner is null) return;
        var tenant = T();
        try
        {
            var id = await SeedPractitioner(tenant);
            await using (var db = Ctx())
            {
                db.PractitionerSpecialties.Add(new PractitionerSpecialty { PractitionerId = id, SpecialtyCode = "PSYCH", IsPrimary = true });
                await db.SaveChangesAsync();
            }
            await using (var db = Ctx())
            {
                db.PractitionerSpecialties.Add(new PractitionerSpecialty { PractitionerId = id, SpecialtyCode = "CARD", IsPrimary = true });
                var act = async () => await db.SaveChangesAsync();
                await act.Should().ThrowAsync<DbUpdateException>("exactly one primary specialty is allowed");
            }
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public async Task Serves_branch_is_true_only_for_an_assigned_in_window_branch()
    {
        if (Owner is null) return;
        var tenant = T();
        var maadi = Guid.NewGuid();
        var dokki = Guid.NewGuid();
        var aswan = Guid.NewGuid();
        try
        {
            var id = await SeedPractitioner(tenant);
            var today = new DateOnly(2026, 7, 26);
            await using (var db = Ctx())
            {
                db.PractitionerBranchAssignments.Add(new PractitionerBranchAssignment { AssignmentId = Guid.NewGuid(), PractitionerId = id, BranchId = maadi, ValidFrom = From, Status = "Active" });
                db.PractitionerBranchAssignments.Add(new PractitionerBranchAssignment { AssignmentId = Guid.NewGuid(), PractitionerId = id, BranchId = dokki, ValidFrom = From, ValidTo = new DateOnly(2026, 6, 30), Status = "Active" });
                await db.SaveChangesAsync();
            }
            await using var verify = Ctx();
            Task<bool> Serves(Guid b) => verify.PractitionerBranchAssignments.AsNoTracking().AnyAsync(a =>
                a.PractitionerId == id && a.BranchId == b && a.Status == "Active" && a.ValidFrom <= today && (a.ValidTo == null || a.ValidTo >= today));

            (await Serves(maadi)).Should().BeTrue();
            (await Serves(dokki)).Should().BeFalse("its window has expired");
            (await Serves(aswan)).Should().BeFalse("not assigned");
        }
        finally { await Cleanup(tenant); }
    }

    private static async Task<Guid> SeedPractitioner(string tenant)
    {
        await using var db = Ctx();
        var now = DateTimeOffset.UtcNow;
        var p = new Practitioner
        {
            PractitionerId = Guid.NewGuid(), TenantId = tenant, UserId = "u-" + Guid.NewGuid().ToString("N")[..8],
            PractitionerType = PractitionerType.Doctor, FullNameEn = "Dr Test", FullNameAr = "د. اختبار",
            Status = PractitionerStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Practitioners.Add(p);
        await db.SaveChangesAsync();
        return p.PractitionerId;
    }

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task Cleanup(string tenant)
    {
        if (Owner is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM provider.practitioner_branch_assignment WHERE practitioner_id IN (SELECT practitioner_id FROM provider.practitioner WHERE tenant_id = {0}); " +
            "DELETE FROM provider.practitioner_specialty WHERE practitioner_id IN (SELECT practitioner_id FROM provider.practitioner WHERE tenant_id = {0}); " +
            "DELETE FROM provider.practitioner WHERE tenant_id = {0};", tenant);
    }
}
