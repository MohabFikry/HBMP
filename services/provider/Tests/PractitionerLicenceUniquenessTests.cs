using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Tests;

/// <summary>
/// 25.2 — ONE practitioner identity, many branch assignments (design 42 §2/§7 rule 4).
///
/// A doctor working at Maadi and Dokki must be one row with two assignments. Without that you get three
/// "Dr Hala Fouad" rows and a roster nobody can reason about: which holds the current licence, which the
/// appointments point at, which to suspend when the licence lapses. D3 makes this sharper rather than
/// softer — six clinics can now each create a practitioner in good faith, none able to see the others'
/// roster.
///
/// Enforced at the DATABASE (`ux_practitioner_license_no`), not only at POST /practitioners: "the endpoint
/// returns 409" is not an invariant a repair script, a data load or a psql session respects.
/// </summary>
public class PractitionerLicenceUniquenessTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");

    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    private static string T() => "t-" + Guid.NewGuid().ToString("N")[..10];

    private static Practitioner New(string tenant, string? licence, bool deleted = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new Practitioner
        {
            PractitionerId = Guid.NewGuid(), TenantId = tenant, UserId = "u-" + Guid.NewGuid().ToString("N")[..8],
            PractitionerType = PractitionerType.Doctor, FullNameEn = "Dr Test", FullNameAr = "د. اختبار",
            LicenseNo = licence, LicenseExpiry = licence is null ? null : new DateOnly(2027, 1, 1),
            Status = PractitionerStatus.Active, IsDeleted = deleted, CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task Cleanup(string tenant)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM provider.practitioner_branch_assignment WHERE practitioner_id IN (SELECT practitioner_id FROM provider.practitioner WHERE tenant_id = {0}); " +
            "DELETE FROM provider.practitioner_specialty WHERE practitioner_id IN (SELECT practitioner_id FROM provider.practitioner WHERE tenant_id = {0}); " +
            "DELETE FROM provider.practitioner WHERE tenant_id = {0};", tenant);
    }

    [SkippableFact]
    public async Task A_second_practitioner_cannot_take_a_licence_number_already_in_use()
    {
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var licence = "LIC-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using (var db = Ctx()) { db.Practitioners.Add(New(tenant, licence)); await db.SaveChangesAsync(); }

            await using (var db = Ctx())
            {
                db.Practitioners.Add(New(tenant, licence));
                var act = async () => await db.SaveChangesAsync();
                await act.Should().ThrowAsync<DbUpdateException>(
                    "one licence belongs to one practitioner — this is the defence against duplicate " +
                    "clinical identities (design 42 §2)");
            }
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task The_uniqueness_rule_crosses_tenants_because_a_licence_does()
    {
        // Deliberate, and worth stating: the index has no tenant_id in it. A medical licence is issued by the
        // Egyptian regulator to a person, not to an organisation — two Mersal tenants holding the same
        // licence number means the same doctor, and letting each keep its own row re-creates exactly the
        // duplicate identity this prevents.
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var a = T();
        var b = T();
        var licence = "LIC-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using (var db = Ctx()) { db.Practitioners.Add(New(a, licence)); await db.SaveChangesAsync(); }
            await using (var db = Ctx())
            {
                db.Practitioners.Add(New(b, licence));
                var act = async () => await db.SaveChangesAsync();
                await act.Should().ThrowAsync<DbUpdateException>();
            }
        }
        finally { await Cleanup(a); await Cleanup(b); }
    }

    [SkippableFact]
    public async Task A_SOFT_DELETED_practitioner_does_not_block_re_registering_their_licence()
    {
        // The index is partial on `is_deleted = false`. Without that, deleting a record in error would lock
        // that licence number out of the platform permanently, and the only remedy would be a hard delete of
        // clinical history — which the platform forbids.
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        var licence = "LIC-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await using (var db = Ctx()) { db.Practitioners.Add(New(tenant, licence, deleted: true)); await db.SaveChangesAsync(); }
            await using (var db = Ctx())
            {
                db.Practitioners.Add(New(tenant, licence));
                var act = async () => await db.SaveChangesAsync();
                await act.Should().NotThrowAsync();
            }
        }
        finally { await Cleanup(tenant); }
    }

    [SkippableFact]
    public async Task Practitioners_with_NO_licence_number_are_not_duplicates_of_one_another()
    {
        // The index is also partial on `license_no IS NOT NULL`. Nurses are recorded without one, and NULL is
        // "not stated", not a value two people can share.
        Skip.If(Owner is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = T();
        try
        {
            await using var db = Ctx();
            db.Practitioners.Add(New(tenant, licence: null));
            db.Practitioners.Add(New(tenant, licence: null));
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }
        finally { await Cleanup(tenant); }
    }

    [Fact]
    public void The_migration_refuses_to_apply_over_existing_duplicates()
    {
        // Merging two clinical identities is a DATA decision — which appointments, which encounters, which
        // specialties survive — never a migration side effect. The migration aborts with the offending
        // licence numbers rather than choosing for whoever runs it.
        var sql = File.ReadAllText(Path.Combine(
            RepoRoot(), "services", "provider", "Infrastructure", "Migrations", "0010_practitioner_licence_unique.sql"));

        sql.Should().Contain("RAISE EXCEPTION", "the backfill check must abort, not warn");
        sql.Should().Contain("HAVING count(*) > 1");
        sql.Should().MatchRegex(
            @"CREATE UNIQUE INDEX IF NOT EXISTS ux_practitioner_license_no[\s\S]*?WHERE is_deleted = false AND license_no IS NOT NULL",
            "the index must be partial on both conditions the tests above rely on");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>
/// 25.2 — specialties stay GLOBAL MASTER DATA (design 42 §1).
///
/// A coordinator ASSIGNS from the seeded 26; creating or renaming one is network-wide reference data that
/// referral routing, reporting and the 14.6 sensitivity defaults all key off. Twenty-six specialties becoming
/// forty because six clinics each invented their own spelling of "Physiotherapy" is not a hypothetical — it
/// is the ordinary fate of any list a branch can append to.
///
/// There is NO specialty-write endpoint at all, which is the strongest form of this guarantee. This test
/// exists so that stays true: it fails the build the moment one appears without going behind provider:write.
/// </summary>
public class SpecialtyCatalogueIsClosedTests
{
    [Fact]
    public void No_endpoint_creates_renames_or_deletes_a_specialty()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "services", "provider", "Api", "Practitioners.cs"));

        // Every route on the specialty RESOURCE itself. The per-practitioner assignment routes
        // (/practitioners/{id}/specialties…) are a different thing and are deliberately allowed.
        foreach (Match m in Regex.Matches(src, @"\.Map(?<verb>Post|Put|Patch|Delete)\(""(?<route>[^""]*)"""))
        {
            var route = m.Groups["route"].Value;
            if (!route.StartsWith("/specialties", StringComparison.Ordinal)) continue;

            throw new Xunit.Sdk.XunitException(
                $"{m.Groups["verb"].Value} {route} writes the specialty catalogue. Specialties are global " +
                "master data (design 42 §1): a branch coordinator assigns from the seeded 26 and must not be " +
                "able to add to it. If this endpoint is intended, it belongs behind provider:write in its own " +
                "group — and this test must be updated to say so deliberately.");
        }
    }

    [Fact]
    public void The_specialty_read_is_still_registered_so_the_scan_is_not_vacuous()
    {
        // Guards the guard: if the specialty routes were renamed wholesale, the loop above would find nothing
        // and pass while proving nothing.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "services", "provider", "Api", "Practitioners.cs"));
        src.Should().Contain(@"read.MapGet(""/specialties""",
            "the catalogue read is what makes it assignable; its absence means this file moved");
    }

    [Fact]
    public void Assigning_an_unknown_specialty_is_refused_rather_than_created()
    {
        // The other half of "closed": the assign endpoints validate the code against the seeded catalogue, so
        // a typo cannot quietly become a 27th specialty held by one practitioner at one clinic.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "services", "provider", "Api", "Practitioners.cs"));
        Regex.Matches(src, @"unknown specialty").Count.Should().BeGreaterThanOrEqualTo(2,
            "both the assign and the promote-to-primary paths must validate against the catalogue");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
