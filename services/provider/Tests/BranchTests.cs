using FluentAssertions;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Tests;

/// <summary>Phase 14.1 — the internal Mersal branch (37 §2). Pure-domain checks always run; the datastore
/// checks (seed presence + idempotency + AR/EN round-trip) are env-gated by <c>PROVIDER_TEST_DB_OWNER</c>
/// (a conn string for the schema owner, with migrations 0001–0005 applied), so DB-less CI skips them.</summary>
public class BranchTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");
    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static readonly string[] SeededCodes = ["ASW", "ALX", "OCT", "MAA", "DOK", "NSR"];

    // The exact seed the migration runs — re-executed here to prove idempotency (ON CONFLICT DO NOTHING).
    private const string SeedSql = """
        INSERT INTO provider.branch (branch_id, branch_code, name_en, name_ar, city) VALUES
            ('0190b100-0000-7000-8000-000000000001', 'ASW', 'Aswan',          'أسوان',            'Aswan'),
            ('0190b100-0000-7000-8000-000000000002', 'ALX', 'Alexandria',     'الإسكندرية',       'Alexandria'),
            ('0190b100-0000-7000-8000-000000000003', 'OCT', '6th of October', 'السادس من أكتوبر', 'Giza'),
            ('0190b100-0000-7000-8000-000000000004', 'MAA', 'Maadi',          'المعادي',          'Cairo'),
            ('0190b100-0000-7000-8000-000000000005', 'DOK', 'Dokki',          'الدقي',            'Giza'),
            ('0190b100-0000-7000-8000-000000000006', 'NSR', 'Nasr City',      'مدينة نصر',        'Cairo')
        ON CONFLICT (branch_code) WHERE is_deleted = false DO NOTHING;
        """;

    [Fact]
    public void Branch_defaults_to_active_in_cairo_timezone()
    {
        var b = new Branch { BranchCode = "TST", NameEn = "Test", NameAr = "اختبار" };
        b.Status.Should().Be(BranchStatus.Active);
        b.Timezone.Should().Be("Africa/Cairo");
    }

    [SkippableFact]
    public async Task The_six_branches_are_seeded_with_en_and_ar_names_and_active_status()
    {
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        var branches = await db.Branches.AsNoTracking().Where(b => SeededCodes.Contains(b.BranchCode)).ToListAsync();

        branches.Should().HaveCount(6);
        branches.Select(b => b.BranchCode).Should().BeEquivalentTo(SeededCodes);
        branches.Should().OnlyContain(b => b.Status == BranchStatus.Active);
        branches.Should().OnlyContain(b => b.NameEn.Length > 0 && b.NameAr.Length > 0);
        // AR names are genuinely Arabic script (round-trips through the datastore unmangled).
        branches.Single(b => b.BranchCode == "MAA").NameAr.Should().Be("المعادي");
        branches.Single(b => b.BranchCode == "ASW").NameAr.Should().Be("أسوان");
    }

    [SkippableFact]
    public async Task Re_running_the_seed_creates_no_duplicates()
    {
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(SeedSql);   // run the seed a second (and third) time
        await db.Database.ExecuteSqlRawAsync(SeedSql);

        var count = await db.Branches.AsNoTracking().CountAsync(b => SeededCodes.Contains(b.BranchCode));
        count.Should().Be(6, "the seed is idempotent (ON CONFLICT DO NOTHING)");
    }

    [SkippableFact]
    public async Task A_branch_round_trips_arabic_and_english_names_through_the_datastore()
    {
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var code = "T" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using (var db = Ctx())
            {
                db.Branches.Add(new Branch
                {
                    BranchId = Guid.NewGuid(), BranchCode = code, NameEn = "Round Trip", NameAr = "ذهاب وإياب",
                    City = "Cairo", Timezone = "Africa/Cairo", OpeningHours = """{"sun":"09:00-17:00"}""",
                    CreatedAt = now, UpdatedAt = now,
                });
                await db.SaveChangesAsync();
            }
            await using (var verify = Ctx())
            {
                var b = await verify.Branches.AsNoTracking().SingleAsync(x => x.BranchCode == code);
                b.NameAr.Should().Be("ذهاب وإياب");
                b.NameEn.Should().Be("Round Trip");
                b.OpeningHours.Should().Contain("09:00-17:00");
            }
        }
        finally
        {
            await using var db = Ctx();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM provider.branch WHERE branch_code = {0}", code);
        }
    }
}
