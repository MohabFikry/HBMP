using FluentAssertions;
using Mersal.Case.Domain;
using Mersal.Case.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Case.Tests;

/// <summary>Case-service at the datastore (env-gated <c>CASE_TEST_DB</c>; needs the hbmp superuser conn — hbmp_app
/// lacks the schema grants). Proves the monotonic case number, the assignment resolver that backs the
/// case-assignment ABAC condition, and that UNASSIGNMENT removes the case from the resolver's active set (immediate
/// revocation, 10 §3.11). Serialized via the case-db collection. No-ops without the env var.</summary>
[Collection("case-db")]
public class CaseIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CASE_TEST_DB");

    private static DbContextOptions<CaseDbContext> Options() =>
        new DbContextOptionsBuilder<CaseDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [SkippableFact]
    public async Task Case_number_is_monotonic_per_year()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = new CaseDbContext(Options());
        var a = await new CaseNoIssuer(db).NextAsync(2026);
        var b = await new CaseNoIssuer(db).NextAsync(2026);
        a.Should().StartWith("CASE-2026-");
        string.CompareOrdinal(b, a).Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Assignment_grants_then_unassignment_revokes_in_the_resolver()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
        var manager = Guid.NewGuid();
        Guid caseId;
        try
        {
            await using (var db = new CaseDbContext(Options()))
            {
                var c = new CaseFile
                {
                    CaseId = Guid.NewGuid(), CaseNo = await new CaseNoIssuer(db).NextAsync(2026),
                    TenantId = tenant, BeneficiaryId = Guid.NewGuid(), Category = CaseCategory.Complex,
                    Status = CaseStatus.Open, OpenedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Cases.Add(c);
                db.Assignments.Add(new CaseAssignment
                {
                    AssignmentId = Guid.NewGuid(), CaseId = c.CaseId, CaseManagerId = manager,
                    AssignedAt = DateTimeOffset.UtcNow, Active = true,
                });
                await db.SaveChangesAsync();
                caseId = c.CaseId;
            }

            await using (var db = new CaseDbContext(Options()))
            {
                var resolver = new AssignmentResolver(db);
                (await resolver.ActiveCaseIdsForAsync(manager)).Should().Contain(caseId.ToString());
                (await resolver.HasActiveAssignmentAsync(caseId, manager)).Should().BeTrue();

                // Unassign → active=false + timestamp.
                var a = await db.Assignments.FirstAsync(x => x.CaseId == caseId && x.CaseManagerId == manager && x.Active);
                a.Active = false;
                a.UnassignedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }

            await using (var db = new CaseDbContext(Options()))
            {
                var resolver = new AssignmentResolver(db);
                (await resolver.ActiveCaseIdsForAsync(manager)).Should().NotContain(caseId.ToString());
                (await resolver.HasActiveAssignmentAsync(caseId, manager)).Should().BeFalse();
            }
        }
        finally
        {
            await using var db = new CaseDbContext(Options());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"case\".case_assignment WHERE case_manager_id = {0};", manager);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"case\".case_file WHERE tenant_id = {0};", tenant);
        }
    }
}
