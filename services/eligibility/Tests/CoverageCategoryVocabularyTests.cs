using FluentAssertions;
using Mersal.Eligibility.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// The benefit category on a coverage projection is a CODE, and the column refuses anything else.
/// </summary>
/// <remarks>
/// <para>
/// It is matched, not shown. Callers send <c>LAB</c>; policy-service publishes <c>LAB</c>; the engine compares
/// on it. Three seeds wrote display names instead — <c>Laboratory</c>, <c>Consultation</c>, <c>Outpatient</c>,
/// and a case fixture's <c>Oncology</c> — and the result was an eligibility engine answering "no active
/// coverage for LAB" to every seeded member who held laboratory cover.
/// </para>
/// <para>
/// <b>Why that hid for so long.</b> The failure is indistinguishable from the truth. "No active coverage" is
/// exactly what the desk sees for a member who genuinely has none, so nothing looked broken — it looked like
/// data. It surfaced only because a dispensing counter could not price a prescription, and PHARMACY was the
/// one category that accidentally worked (<c>Pharmacy</c> matches <c>PHARMACY</c> case-insensitively).
/// </para>
/// <para>
/// So the guard is in the DATABASE, not in a reviewer's attention. A vocabulary that is closed in the design
/// and open in the column is one bad INSERT away from telling a refugee they are not covered.
/// </para>
/// </remarks>
[Collection("eligibility-db")]
public class CoverageCategoryVocabularyTests
{
    [SkippableFact]
    public async Task A_display_name_cannot_be_written_where_a_code_belongs()
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");

        var act = async () => await InsertCategoryAsync("Laboratory");

        // 23514 = check_violation. The seed that did this shipped and ran; the column now refuses it.
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");
    }

    [Theory]
    [InlineData("Consultation")]
    [InlineData("Outpatient")]
    [InlineData("Oncology")]
    [InlineData("lab")]
    public async Task Every_shape_the_seeds_used_is_refused(string wrong)
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");

        // Including the lower-case one. 'Pharmacy' matched 'PHARMACY' case-insensitively and that near-miss
        // is what made the defect look like a pharmacy problem rather than a vocabulary problem.
        var act = async () => await InsertCategoryAsync(wrong);
        await act.Should().ThrowAsync<PostgresException>();
    }

    [Theory]
    [InlineData("CONSULT")]
    [InlineData("LAB")]
    [InlineData("IMAGING")]
    [InlineData("PHARMACY")]
    [InlineData("REFERRAL")]
    public async Task The_canonical_five_are_accepted(string code)
    {
        Skip.If(EligibilityApiFactory.Db is null, "ELIGIBILITY_TEST_DB not set — DB integration test skipped.");

        // The constraint has to admit everything the platform actually uses, or the fix is an outage.
        var id = await InsertCategoryAsync(code);
        await DeleteAsync(id);
    }

    private static async Task<Guid> InsertCategoryAsync(string category)
    {
        await using var db = Ctx();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO eligibility.coverage_projection
                (coverage_id, beneficiary_id, benefit_category, policy_no, status, effective_from, tenant_id)
              VALUES ({0}, {1}, {2}, 'POL-VOCAB-TEST', 'Active', CURRENT_DATE, '11111111-1111-1111-1111-111111111111')",
            id, Guid.NewGuid(), category);
        return id;
    }

    private static async Task DeleteAsync(Guid id)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM eligibility.coverage_projection WHERE coverage_id = {0}", id);
    }

    private static EligibilityDbContext Ctx() =>
        new(new DbContextOptionsBuilder<EligibilityDbContext>()
            .UseNpgsql(EligibilityApiFactory.Db).UseSnakeCaseNamingConvention().Options);
}
