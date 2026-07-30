using FluentAssertions;
using Mersal.Emr.Domain;
using Mersal.Emr.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Tests;

/// <summary>Phase 4.1 clinical persistence + treating-relationship at the datastore (env-gated <c>EMR_TEST_DB</c>).
/// Proves the row-level half of US-030 (the clinician who owns an encounter treats the patient; another does not)
/// and the encounter→note→diagnosis write flow. Self-cleans by beneficiary scope tag.</summary>
public class ClinicalIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("EMR_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static DbContextOptions<EmrDbContext> Options() =>
        new DbContextOptionsBuilder<EmrDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [SkippableFact]
    public async Task Owning_clinician_treats_but_another_does_not()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            await SeedEncounter(beneficiary, createdBy: "dr-owner");

            await using var ctx = new EmrDbContext(Options());
            var treating = new TreatingRelationship(ctx);
            (await treating.TreatsAsync("dr-owner", null, beneficiary)).Should().BeTrue();
            (await treating.TreatsAsync("dr-stranger", null, beneficiary)).Should().BeFalse();
        }
        finally { await Cleanup(beneficiary); }
    }

    [SkippableFact]
    public async Task Encounter_note_and_diagnosis_persist_and_read_back()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var beneficiary = Guid.NewGuid();
        try
        {
            var encId = await SeedEncounter(beneficiary, createdBy: "dr-owner");

            await using (var ctx = new EmrDbContext(Options()))
            {
                ctx.Notes.Add(new EmrNote
                {
                    NoteId = Guid.NewGuid(), EncounterId = encId, NoteType = NoteType.SOAP, TenantId = Tenant,
                    Assessment = "Acute pharyngitis", AuthoredBy = "dr-owner", AuthoredAt = DateTimeOffset.UtcNow,
                });
                ctx.Diagnoses.Add(new Diagnosis
                {
                    DiagnosisId = Guid.NewGuid(), EncounterId = encId, IcdCode = "J02.9", TenantId = Tenant,
                    DiagnosisRank = DiagnosisRank.Primary, ClinicalStatus = ClinicalStatus.Active,
                    RecordedBy = "dr-owner", RecordedAt = DateTimeOffset.UtcNow,
                });
                await ctx.SaveChangesAsync();
            }

            await using var verify = new EmrDbContext(Options());
            (await verify.Notes.AsNoTracking().CountAsync(n => n.EncounterId == encId)).Should().Be(1);
            var dx = await verify.Diagnoses.AsNoTracking().SingleAsync(d => d.EncounterId == encId);
            dx.IcdCode.Should().Be("J02.9");
        }
        finally { await Cleanup(beneficiary); }
    }

    private static async Task<Guid> SeedEncounter(Guid beneficiary, string createdBy)
    {
        var encId = Guid.NewGuid();
        await using var ctx = new EmrDbContext(Options());
        ctx.Encounters.Add(new Encounter
        {
            EncounterId = encId, EncounterNo = $"ENC-TEST-{encId.ToString()[..8]}", BeneficiaryId = beneficiary,
            // 24.x — the fixture stamps the tenant because it builds the row through a PLAIN DbContext,
            // with none of the interceptors the API composes. Production is stamped by
            // TenantStampingInterceptor from the bound request; a fixture that skips that has to supply
            // what the interceptor would have, or it writes a row belonging to no tenant — which the
            // ck_encounter_tenant_not_blank constraint now refuses, and which used to be accepted in
            // silence. Test data that could not exist in production tests nothing about production.
            TenantId = Tenant,
            Status = EncounterStatus.InProgress, StartedAt = DateTimeOffset.UtcNow, CreatedBy = createdBy,
        });
        await ctx.SaveChangesAsync();
        return encId;
    }

    private static async Task Cleanup(Guid beneficiary)
    {
        await using var ctx = new EmrDbContext(Options());
        var encIds = await ctx.Encounters.Where(e => e.BeneficiaryId == beneficiary).Select(e => e.EncounterId).ToListAsync();
        await ctx.Diagnoses.Where(d => encIds.Contains(d.EncounterId)).ExecuteDeleteAsync();
        await ctx.Notes.Where(n => encIds.Contains(n.EncounterId)).ExecuteDeleteAsync();
        await ctx.Encounters.Where(e => e.BeneficiaryId == beneficiary).ExecuteDeleteAsync();
    }
}
