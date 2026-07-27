using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.3b at the datastore (env-gated <c>POLICY_TEST_DB</c>, migration 0010 applied).
///
/// The classification rule is only worth as much as its weakest path. The endpoint refuses to lower a
/// visibility, but a repair script or a psql session would not — and the failure is silent, because a
/// downgraded document looks exactly like one that was always administrative. These attempts are therefore
/// made directly through EF, with no endpoint in the way.
/// </summary>
public class PolicyDocumentStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static PolicyDocument Fresh(
        NoteVisibility visibility = NoteVisibility.Clinical,
        DocumentClass documentClass = DocumentClass.DischargeSummary) => new()
    {
        LinkId = Guid.NewGuid(), TenantId = Tenant, Scope = NoteScope.Member, ScopeRef = Guid.NewGuid(),
        DocumentId = Guid.NewGuid(), VersionNo = 1,
        DocumentClass = documentClass, VisibilityClass = visibility,
        Title = "Discharge summary", DocumentDate = new DateOnly(2019, 4, 12),
        UploadedByUserId = Guid.NewGuid(), UploadedByUsername = "officer.mona", UploadedByDisplay = "Mona Adel",
        UploadedAt = DateTimeOffset.UtcNow, Status = DocumentLinkStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<Guid> Insert(PolicyDocument doc)
    {
        await using var db = Ctx();
        db.PolicyDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.LinkId;
    }

    private static async Task Cleanup(params Guid[] linkIds)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE policy.policy_document DISABLE TRIGGER trg_policy_document_immutable");
        try
        {
            foreach (var id in linkIds)
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE policy.policy_document SET supersedes_link_id = NULL WHERE link_id = {0}", id);
            foreach (var id in linkIds)
                await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.policy_document WHERE link_id = {0}", id);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE policy.policy_document ENABLE TRIGGER trg_policy_document_immutable");
        }
    }

    // ---- Visibility: raise, never lower ------------------------------------------------------------------

    [SkippableFact]
    public async Task Visibility_can_never_be_lowered_by_any_path()
    {
        // The silent failure this exists to prevent: a downgraded clinical document looks exactly like one
        // that was always administrative, and is readable by finance and the call centre from then on.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh(NoteVisibility.Clinical));
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            doc.VisibilityClass = NoteVisibility.Administrative;

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("raised but never lowered");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task Visibility_can_be_raised()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh(NoteVisibility.Clinical));
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            doc.VisibilityClass = NoteVisibility.Restricted;
            await db.SaveChangesAsync();

            (await db.PolicyDocuments.AsNoTracking().FirstAsync(d => d.LinkId == id))
                .VisibilityClass.Should().Be(NoteVisibility.Restricted);
        }
        finally { await Cleanup(id); }
    }

    // ---- Nothing is ever deleted -------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_document_link_can_never_be_deleted()
    {
        // "Wrong member" is a reason to MARK a document, not to make the mistake unfindable — and the
        // withdrawal is frequently the thing a later review needs to see.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            db.PolicyDocuments.Remove(doc);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("withdraw");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task The_uploader_signature_can_never_be_rewritten()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            doc.UploadedByUsername = "someone.else";

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("signed");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task A_link_cannot_be_repointed_at_another_member_or_another_blob()
    {
        // Repointing would transplant one person's medical record onto another, silently.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            doc.ScopeRef = Guid.NewGuid();

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("immutable");
        }
        finally { await Cleanup(id); }
    }

    // ---- Versioning ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_re_upload_supersedes_the_prior_version_without_deleting_it()
    {
        // The superseded version is what a dispute about "which report did you act on" is settled with.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var firstId = await Insert(Fresh());
        Guid secondId = Guid.Empty;
        try
        {
            await using (var db = Ctx())
            {
                var prior = await db.PolicyDocuments.FirstAsync(d => d.LinkId == firstId);
                prior.Status = DocumentLinkStatus.Superseded;

                var next = Fresh();
                next.ScopeRef = prior.ScopeRef;
                next.VersionNo = 2;
                next.SupersedesLinkId = firstId;
                db.PolicyDocuments.Add(next);
                await db.SaveChangesAsync();
                secondId = next.LinkId;
            }

            await using var check = Ctx();
            var rows = await check.PolicyDocuments.AsNoTracking()
                .Where(d => d.LinkId == firstId || d.LinkId == secondId).ToListAsync();
            rows.Should().HaveCount(2, "the prior version is kept, never deleted");
            rows.Single(d => d.LinkId == firstId).Status.Should().Be(DocumentLinkStatus.Superseded);
            rows.Single(d => d.LinkId == secondId).VersionNo.Should().Be(2);
        }
        finally { await Cleanup(secondId, firstId); }
    }

    // ---- Withdrawal ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_withdrawal_without_a_reason_is_rejected_by_the_database()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            doc.Status = DocumentLinkStatus.Withdrawn;
            doc.WithdrawnByUserId = Guid.NewGuid();
            doc.WithdrawnAt = DateTimeOffset.UtcNow;   // …but no reason

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_pdoc_withdrawal_complete");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task A_withdrawn_document_keeps_its_row_and_its_metadata()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using (var db = Ctx())
            {
                var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
                doc.Status = DocumentLinkStatus.Withdrawn;
                doc.WithdrawnByUserId = Guid.NewGuid();
                doc.WithdrawnByUsername = "supervisor.amal";
                doc.WithdrawnAt = DateTimeOffset.UtcNow;
                doc.WithdrawalReason = "attached to the wrong member";
                await db.SaveChangesAsync();
            }

            await using var check = Ctx();
            var saved = await check.PolicyDocuments.AsNoTracking().FirstAsync(d => d.LinkId == id);
            saved.Status.Should().Be(DocumentLinkStatus.Withdrawn);
            saved.Title.Should().NotBeNullOrWhiteSpace();
            saved.DocumentId.Should().NotBeEmpty("the bytes stay too — the mistake must remain findable");
            saved.WithdrawalReason.Should().Be("attached to the wrong member");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task Verification_is_all_or_nothing()
    {
        // A verified_at with no verifier is unattributable, which is worse than unverified.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var doc = await db.PolicyDocuments.FirstAsync(d => d.LinkId == id);
            doc.VerifiedAt = DateTimeOffset.UtcNow;   // …with no verifier

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_pdoc_verification_complete");
        }
        finally { await Cleanup(id); }
    }

    // ---- Clinical ordering --------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Past_medical_history_orders_by_document_date_not_upload_order()
    {
        // A 2019 discharge summary scanned in today belongs in 2019 on the member's history. Ordering by
        // upload would make a member's clinical history read backwards.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var scopeRef = Guid.NewGuid();
        var older = Fresh(); older.ScopeRef = scopeRef; older.DocumentDate = new DateOnly(2019, 4, 12);
        var newer = Fresh(); newer.ScopeRef = scopeRef; newer.DocumentDate = new DateOnly(2024, 9, 1);

        // Uploaded in the OPPOSITE order to their clinical dates, which is the realistic case.
        newer.UploadedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        older.UploadedAt = DateTimeOffset.UtcNow;
        await Insert(newer);
        await Insert(older);
        try
        {
            await using var db = Ctx();
            var ordered = await db.PolicyDocuments.AsNoTracking()
                .Where(d => d.ScopeRef == scopeRef)
                .OrderByDescending(d => d.DocumentDate)
                .ToListAsync();

            ordered[0].DocumentDate.Should().Be(new DateOnly(2024, 9, 1));
            ordered[1].DocumentDate.Should().Be(new DateOnly(2019, 4, 12));
            // …and that is NOT the upload order.
            ordered[0].UploadedAt.Should().BeBefore(ordered[1].UploadedAt);
        }
        finally { await Cleanup(older.LinkId, newer.LinkId); }
    }
}
