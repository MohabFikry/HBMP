using FluentAssertions;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mersal.Policy.Tests;

/// <summary>
/// Phase 19.3 at the datastore (env-gated <c>POLICY_TEST_DB</c>, migration 0009 applied).
///
/// The endpoints never write a body or a signature after creation, so none of these attempts can be made
/// through the API at all. That is exactly why they are made HERE, directly through EF: "the endpoint does not
/// do it" is not an invariant — a repair script, a future endpoint or a psql session would walk straight past
/// it, and this surface is the one read back in disputes months later by people who were not there.
/// </summary>
public class NoteStoreTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("POLICY_TEST_DB");
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static PolicyDbContext Ctx() => new(new DbContextOptionsBuilder<PolicyDbContext>()
        .UseNpgsql(Db).UseSnakeCaseNamingConvention().Options);

    private static Note Fresh(NoteVisibility visibility = NoteVisibility.Administrative) => new()
    {
        NoteId = Guid.NewGuid(), TenantId = Tenant, Scope = NoteScope.Member, ScopeRef = Guid.NewGuid(),
        NoteType = NoteType.General, Body = "The member was advised of the waiting period.",
        VisibilityClass = visibility,
        AuthoredByUserId = Guid.NewGuid(), AuthoredByUsername = "officer.mona", AuthoredByDisplay = "Mona Adel",
        AuthoredAt = DateTimeOffset.UtcNow, Status = NoteStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<Guid> Insert(Note note)
    {
        await using var db = Ctx();
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        return note.NoteId;
    }

    private static async Task Cleanup(Guid noteId)
    {
        await using var db = Ctx();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.note DISABLE TRIGGER trg_note_append_only");
        try
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM policy.note WHERE note_id = {0}", noteId);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE policy.note ENABLE TRIGGER trg_note_append_only");
        }
    }

    // ---- Append-only -------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_notes_body_can_never_be_edited()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.Body = "Actually, the member was never advised.";   // rewriting what was recorded

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("append-only");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task A_note_can_never_be_deleted()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            db.Notes.Remove(note);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("cancel");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task A_signature_can_never_be_rewritten()
    {
        // The point of snapshotting the author is that it survives them being renamed or de-provisioned. That
        // guarantee is worthless if the snapshot itself can be edited afterwards.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.AuthoredByUsername = "someone.else";

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("signed");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task What_a_note_is_about_cannot_be_reassigned()
    {
        // Moving a note to another member would silently transplant a statement about one person onto another.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.ScopeRef = Guid.NewGuid();

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("reassigned");
        }
        finally { await Cleanup(id); }
    }

    // ---- Visibility may be raised, never lowered ---------------------------------------------------------

    [SkippableFact]
    public async Task Visibility_can_be_raised()
    {
        // Realising afterwards that a note is clinical must be fixable — that direction only ever narrows who
        // can read it.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh(NoteVisibility.Administrative));
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.VisibilityClass = NoteVisibility.Clinical;
            await db.SaveChangesAsync();

            (await db.Notes.AsNoTracking().FirstAsync(n => n.NoteId == id))
                .VisibilityClass.Should().Be(NoteVisibility.Clinical);
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task Visibility_can_never_be_lowered()
    {
        // Lowering it retroactively exposes a clinical body to roles that were correctly denied it — and the
        // exposure is invisible, because nothing about the note looks different afterwards.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh(NoteVisibility.Restricted));
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.VisibilityClass = NoteVisibility.Administrative;

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("raised but never lowered");
        }
        finally { await Cleanup(id); }
    }

    // ---- Cancellation ------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Cancelling_is_the_one_permitted_mutation()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using (var db = Ctx())
            {
                var note = await db.Notes.FirstAsync(n => n.NoteId == id);
                note.Status = NoteStatus.Cancelled;
                note.CancelledByUserId = Guid.NewGuid();
                note.CancelledByUsername = "supervisor.amal";
                note.CancelledAt = DateTimeOffset.UtcNow;
                note.CancellationReason = "recorded against the wrong member";
                await db.SaveChangesAsync();
            }

            await using var check = Ctx();
            var saved = await check.Notes.AsNoTracking().FirstAsync(n => n.NoteId == id);
            saved.Status.Should().Be(NoteStatus.Cancelled);
            saved.Body.Should().NotBeNullOrWhiteSpace("a cancelled note keeps its body — struck through, not erased");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task A_cancellation_without_a_reason_is_rejected_by_the_database()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.Status = NoteStatus.Cancelled;
            note.CancelledByUserId = Guid.NewGuid();
            note.CancelledAt = DateTimeOffset.UtcNow;   // …but no reason

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_note_cancellation_complete");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task A_cancelled_note_can_never_be_reinstated()
    {
        // Un-cancelling would let a withdrawn statement quietly come back into force. A correction is a new note.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var note = Fresh();
        note.Status = NoteStatus.Cancelled;
        note.CancelledByUserId = Guid.NewGuid();
        note.CancelledByUsername = "supervisor.amal";
        note.CancelledAt = DateTimeOffset.UtcNow;
        note.CancellationReason = "wrong member";
        var id = await Insert(note);
        try
        {
            await using var db = Ctx();
            var saved = await db.Notes.FirstAsync(n => n.NoteId == id);
            saved.Status = NoteStatus.Active;
            saved.CancelledByUserId = null;
            saved.CancelledByUsername = null;
            saved.CancelledAt = null;
            saved.CancellationReason = null;

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.MessageText.Should().Contain("cannot be reinstated");
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task An_active_note_may_not_carry_cancellation_fields()
    {
        // Otherwise it renders as withdrawn everywhere while still being in force.
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var note = Fresh();
        note.CancellationReason = "half-cancelled";
        var id = note.NoteId;
        try
        {
            await using var db = Ctx();
            db.Notes.Add(note);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            ex.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be("ck_note_active_is_clean");
        }
        finally { await Cleanup(id); }
    }

    // ---- Pinning changes no content ----------------------------------------------------------------------

    [SkippableFact]
    public async Task Pinning_is_allowed_because_it_changes_no_content()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var id = await Insert(Fresh());
        try
        {
            await using var db = Ctx();
            var note = await db.Notes.FirstAsync(n => n.NoteId == id);
            note.Pinned = true;
            await db.SaveChangesAsync();

            (await db.Notes.AsNoTracking().FirstAsync(n => n.NoteId == id)).Pinned.Should().BeTrue();
        }
        finally { await Cleanup(id); }
    }

    [SkippableFact]
    public async Task An_empty_body_is_rejected()
    {
        Skip.If(Db is null, "POLICY_TEST_DB not set — DB integration test skipped.");
        var note = Fresh();
        note.Body = "   ";
        var id = note.NoteId;
        try
        {
            await using var db = Ctx();
            db.Notes.Add(note);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally { await Cleanup(id); }
    }
}
