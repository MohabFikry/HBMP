using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Provider.Api;
using Mersal.Provider.Domain;
using Mersal.Provider.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Provider.Tests;

/// <summary>
/// 0014 — the practitioner's OPERATIONAL history, and the two things that must stay true about who can read it.
///
/// <para><b>Why it exists.</b> Licence changes have been audited since 25.2, and the audit trail is behind
/// <c>audit:read</c> — Security, Compliance, the DPO. That is right: it is hash-chained evidence whose own
/// reads are audited. It also left the coordinator who administers a licence unable to ask who last renewed
/// it, about a record on their own clinic's roster. The answer is a domain history under the SAME authority
/// that maintains the record, not a wider grant on the compliance store.</para>
/// </summary>
public class PractitionerHistoryTests
{
    private static readonly string? Owner = Environment.GetEnvironmentVariable("PROVIDER_TEST_DB_OWNER");

    private static ProviderDbContext Ctx() =>
        new(new DbContextOptionsBuilder<ProviderDbContext>().UseNpgsql(Owner).UseSnakeCaseNamingConvention().Options);

    // ---- the two authority assertions ------------------------------------------------------------------

    [Fact]
    public void THE_ONE_THAT_MATTERS_the_timeline_never_widens_audit_read()
    {
        // If this endpoint ever appears alongside `audit:read`, the fix has become the thing it was avoiding:
        // clinic staff holding the compliance trail to answer an operational question.
        //
        // Matched inside an AUTHORIZATION EXPRESSION, not anywhere in the file. A bare search for the string
        // also hits the comment above the route explaining why the scope is not used — so the first version of
        // this test failed on the very prose describing the rule it enforces.
        var src = Source("Api", "Practitioners.cs");

        Regex.IsMatch(src, @"(RequireAuthorization|HbmpPolicies\.\w+)\([^)]*audit:read").Should().BeFalse(
            "the operational timeline is served from provider's own history twin — the hash-chained audit " +
            "store stays behind audit:read, for Security/Compliance/DPO only");
    }

    [Fact]
    public void The_timeline_sits_on_the_LICENCE_MAINTAINING_group_not_the_read_group()
    {
        // The snapshot contains license_no, which ToView masks for anyone without a licence-maintaining
        // scope. Reception and the call centre hold `practitioner:read` for the booking pickers — so putting
        // the history on the read group would hand the front desk, through the back door, the exact field the
        // projection exists to withhold.
        var src = Source("Api", "Practitioners.cs");

        var historyRoute = src.IndexOf(@"MapGet(""/practitioners/{id:guid}/history""", StringComparison.Ordinal);
        historyRoute.Should().BeGreaterThan(0, "the history route must exist");

        // The registration immediately preceding it must be `write.`, never `read.`.
        var registrar = src[..historyRoute].TrimEnd()[^6..];
        registrar.Should().EndWith("write.",
            "a history that leaks a masked field to a wider audience than the projection is not a history, " +
            "it is a bypass");
    }

    // ---- the trigger ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Every_change_to_a_practitioner_writes_a_history_row_naming_the_actor()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");

        var tenant = $"hist-{Guid.NewGuid()}";
        var id = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                db.Practitioners.Add(new Practitioner
                {
                    PractitionerId = id, TenantId = tenant, UserId = $"u-{id}",
                    PractitionerType = PractitionerType.Doctor,
                    FullNameEn = "Hala Fouad", FullNameAr = "هالة فؤاد",
                    LicenseNo = $"LIC-{id.ToString()[..8]}", LicenseExpiry = new DateOnly(2027, 3, 31),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = "u-creator",
                });
                await db.SaveChangesAsync();
            }

            // A renewal, with the actor stamped exactly as the licence endpoint stamps it.
            await using (var db = Ctx())
            {
                var p = await db.Practitioners.SingleAsync(x => x.PractitionerId == id);
                p.LicenseExpiry = new DateOnly(2028, 3, 31);
                p.UpdatedAt = DateTimeOffset.UtcNow;
                p.UpdatedBy = "u-coordinator";
                p.UpdatedByName = "Mona Saleh";
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var rows = await db.PractitionerHistory.AsNoTracking()
                    .Where(h => h.PractitionerId == id).OrderBy(h => h.HistoryId).ToListAsync();

                rows.Should().HaveCount(2, "an INSERT and an UPDATE each leave a snapshot");
                rows[0].Operation.Should().Be("INSERT");
                rows[1].Operation.Should().Be("UPDATE");

                var entries = rows.ConvertAll(PractitionerHistoryView.From);

                entries[0].LicenseExpiry.Should().Be("2027-03-31");
                entries[1].LicenseExpiry.Should().Be("2028-03-31",
                    "the timeline's whole purpose is showing that this date moved");

                // The question people actually ask. Before 0014 the row recorded when it changed and never
                // who, so a complete timeline still could not answer it.
                entries[1].ActorSubject.Should().Be("u-coordinator");
                entries[1].ActorName.Should().Be("Mona Saleh");
            }
        }
        finally { await CleanupAsync(tenant, id); }
    }

    [SkippableFact]
    public async Task The_timeline_projects_the_administered_fields_and_not_the_whole_row()
    {
        Skip.If(Owner is null, "test DB not configured — set PROVIDER_TEST_DB_OWNER to run this DB integration test.");

        var tenant = $"hist-{Guid.NewGuid()}";
        var id = Guid.NewGuid();
        try
        {
            await using (var db = Ctx())
            {
                db.Practitioners.Add(new Practitioner
                {
                    PractitionerId = id, TenantId = tenant, UserId = $"u-{id}",
                    PractitionerType = PractitionerType.Doctor,
                    FullNameEn = "Hala Fouad", FullNameAr = "هالة فؤاد",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = Ctx())
            {
                var row = await db.PractitionerHistory.AsNoTracking()
                    .SingleAsync(h => h.PractitionerId == id);

                // The stored snapshot IS the whole row — that is what makes the trigger survivable when the
                // table gains a column.
                row.RowSnapshot.Should().Contain("full_name_en");

                // What is SERVED is not. A timeline is a record of changes, not a second route to the record,
                // and the staff directory is not what a coordinator opened it to see.
                var view = PractitionerHistoryView.From(row);
                var json = System.Text.Json.JsonSerializer.Serialize(view);
                json.Should().NotContain("Hala Fouad");
                json.Should().NotContain("هالة فؤاد");
                json.Should().NotContain($"u-{id}", "the staff member's user id is not an administered field");
            }
        }
        finally { await CleanupAsync(tenant, id); }
    }

    private static async Task CleanupAsync(string tenant, Guid id)
    {
        if (Owner is null) return;
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM provider.practitioner_history WHERE tenant_id = {0}; " +
            "DELETE FROM provider.practitioner WHERE tenant_id = {0};", tenant);
        _ = id;
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "services", "provider", .. parts]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
