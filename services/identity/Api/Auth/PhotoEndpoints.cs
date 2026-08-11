using System.Security.Claims;
using Mersal.Audit.Client;
using Mersal.Authz;
using Mersal.Identity.Domain;
using Mersal.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Phase 28.15 — a staff member's avatar: read it, set your own, and (as an administrator) set somebody's.
///
/// <para>
/// ============================================================================================================
/// THIS IS NOT THE BENEFICIARY PHOTO, AND MUST NOT GROW INTO IT
/// ============================================================================================================
/// The platform's other photograph endpoint — <c>GET /patients/{id}/photo</c> — resolves a short-TTL signed
/// URL from MinIO, is gated by an allow-list narrower than the profile's, and writes an audit event on every
/// READ, because a refugee's face is identity-sensitive data whose every disclosure is answerable to a
/// data-subject request (design 39 §5). None of that applies here. This is a member of staff's own picture
/// of themselves, shown to the colleagues they work with, in the same category as the display name it sits
/// beside. Writes are audited; reads are not, and treating a colleague's avatar as a disclosure event would
/// bury the reads that genuinely are.
/// </para>
///
/// <para>
/// ============================================================================================================
/// WHAT IS CHECKED, AND WHY EACH CHECK IS THERE
/// ============================================================================================================
///   * SIZE, before anything else — an unbounded body is the cheapest denial of service there is, and the
///     browser has already downscaled, so a large one is either a bug or an attempt.
///   * The MAGIC BYTES, not the declared content type. `Content-Type` is a claim made by whoever is
///     uploading; the first bytes of the file are what the browser will actually act on.
///   * `X-Content-Type-Options: nosniff` on the way out, so a browser cannot be persuaded to reinterpret
///     stored bytes as script. Between the sniff on write and the header on read, there is no point at which
///     "an image" is taken on trust.
/// </para>
/// </summary>
public static class PhotoEndpoints
{
    /// <summary>512 KB. Generous for the 512px square the browser sends, small enough that no request here is
    /// a memory concern. Matched by the CHECK constraint in migration 0038.</summary>
    public const int MaxBytes = 512 * 1024;

    private const string Png = "image/png";
    private const string Jpeg = "image/jpeg";
    private const string Webp = "image/webp";

    public static void MapUserPhotos(this WebApplication app)
    {
        var me = app.MapGroup("/identity/me").RequireAuthorization(IdentityAdminPolicies.Authenticated);

        // ---- read -------------------------------------------------------------------------------------
        //
        // ANY authenticated caller may read ANY staff avatar, and that is the intended breadth: the picture
        // exists to be shown beside a name in a roster, a worklist and an audit row, and a permission check
        // per avatar would make those screens issue one authorization decision per face. It discloses what
        // the display name already does.
        app.MapGet("/identity/users/{id:guid}/photo", async (Guid id, IdentityStoreDbContext db, HttpContext http) =>
        {
            var photo = await db.UserPhotos.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == id, http.RequestAborted);
            // 404 rather than a placeholder image: "there is no photo" is a fact the client acts on by
            // rendering initials, and a served placeholder would make that decision here instead.
            if (photo is null) return Results.NotFound();

            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Private, but cacheable for the session — an avatar in a list is fetched once per face per page.
            http.Response.Headers.CacheControl = "private, max-age=300";
            return Results.File(photo.Bytes, photo.ContentType);
        }).RequireAuthorization(IdentityAdminPolicies.Authenticated);

        // ---- self-service -----------------------------------------------------------------------------
        me.MapPut("/photo", async (HttpContext http, IdentityStoreDbContext db, IAuditClient audit, TimeProvider clock) =>
        {
            if (SubjectOf(http) is not { } userId) return Results.Unauthorized();
            return await StoreAsync(http, db, audit, clock, userId, actor: userId.ToString(), onBehalf: false);
        });

        me.MapDelete("/photo", async (HttpContext http, IdentityStoreDbContext db, IAuditClient audit) =>
        {
            if (SubjectOf(http) is not { } userId) return Results.Unauthorized();
            return await RemoveAsync(db, audit, userId, actor: userId.ToString(), http.RequestAborted);
        });

        // ---- administrative ---------------------------------------------------------------------------
        //
        // An administrator setting a colleague's photo is an ordinary act — somebody has sent a headshot to
        // HR and does not administer their own account. It is audited with the actor AND the subject,
        // because "who chose the picture on my profile" is a question the person in it may reasonably ask.
        // The SAME group policy the rest of `/identity/admin` uses: bearer, an `admin:*` scope, and MFA.
        // The per-action `admin:write` check is layer two, exactly as `AdminEndpoints` does it — a route
        // group is the wrong place for the whole control, because the control then depends on the group
        // being wired correctly.
        var admin = app.MapGroup("/identity/admin/users/{id:guid}")
            .RequireAuthorization(IdentityAdminPolicies.Admin);

        admin.MapPut("/photo", async (Guid id, HttpContext http, IdentityStoreDbContext db, IAuditClient audit, TimeProvider clock) =>
        {
            if (RequiresWrite(http) is { } denied) return denied;
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == id, http.RequestAborted))
                return Results.Problem(statusCode: 404, title: "not-found");
            return await StoreAsync(http, db, audit, clock, id, ActorOf(http), onBehalf: true);
        });

        admin.MapDelete("/photo", async (Guid id, HttpContext http, IdentityStoreDbContext db, IAuditClient audit) =>
        {
            if (RequiresWrite(http) is { } denied) return denied;
            return await RemoveAsync(db, audit, id, ActorOf(http), http.RequestAborted);
        });
    }

    private static async Task<IResult> StoreAsync(
        HttpContext http, IdentityStoreDbContext db, IAuditClient audit, TimeProvider clock,
        Guid subjectUserId, string actor, bool onBehalf)
    {
        // Checked BEFORE reading, when the sender declared a length. Reading an unbounded body to discover it
        // is too large is the denial of service the limit exists to prevent.
        if (http.Request.ContentLength is > MaxBytes)
            return ProblemResults.Unprocessable("photo-too-large",
                $"a photo must be {MaxBytes / 1024} KB or smaller");

        // BOUNDED, not a plain CopyToAsync. `Content-Length` above is a claim like any other header: it can be
        // wrong, and under chunked encoding it is absent entirely. Copying the whole body first and measuring
        // it afterwards would be doing the unbounded read that the limit exists to prevent — the check would
        // read as protection while providing none.
        var bytes = await ReadBoundedAsync(http.Request.Body, MaxBytes, http.RequestAborted);
        if (bytes is null)
            return ProblemResults.Unprocessable("photo-too-large",
                $"a photo must be {MaxBytes / 1024} KB or smaller");
        if (bytes.Length == 0) return ProblemResults.Invalid("photo-empty");

        // THE MAGIC BYTES DECIDE, not the header. A `Content-Type` is a claim by the uploader; these are what
        // a browser would act on if the bytes were ever served back.
        var sniffed = Sniff(bytes);
        if (sniffed is null)
            return ProblemResults.Unprocessable("photo-not-an-image",
                "the file is not a PNG, JPEG or WebP image");

        var now = clock.GetUtcNow();
        var existing = await db.UserPhotos.FirstOrDefaultAsync(p => p.UserId == subjectUserId, http.RequestAborted);
        if (existing is null)
        {
            db.UserPhotos.Add(new UserPhoto
            {
                UserId = subjectUserId, ContentType = sniffed, Bytes = bytes, ByteSize = bytes.Length,
                UpdatedAt = now, UpdatedBy = actor,
            });
        }
        else
        {
            existing.ContentType = sniffed;
            existing.Bytes = bytes;
            existing.ByteSize = bytes.Length;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actor;
        }
        await db.SaveChangesAsync(http.RequestAborted);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "identity.user_photo", EntityId = subjectUserId.ToString(), Action = AuditAction.Update,
            ActorUserId = actor,
            // The distinction that makes this row worth reading later: somebody changing their own picture is
            // routine, an administrator changing somebody else's is a thing that person may query.
            DecisionOutcome = onBehalf ? "PhotoSetByAdministrator" : "PhotoSetBySelf",
            AfterState = $"{{\"contentType\":\"{sniffed}\",\"bytes\":{bytes.Length}}}",
            Purpose = "staff-directory", Severity = AuditSeverity.Info,
        }, http.RequestAborted);

        return Results.Ok(new { contentType = sniffed, byteSize = bytes.Length });
    }

    private static async Task<IResult> RemoveAsync(
        IdentityStoreDbContext db, IAuditClient audit, Guid subjectUserId, string actor, CancellationToken ct)
    {
        var photo = await db.UserPhotos.FirstOrDefaultAsync(p => p.UserId == subjectUserId, ct);
        // Idempotent: removing a photo that is not there is the state the caller asked for.
        if (photo is null) return Results.NoContent();

        db.UserPhotos.Remove(photo);
        await db.SaveChangesAsync(ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            // `Update`, not a delete action: the enum has none, and what changed is the user's photo STATE
            // rather than a record being retired. The outcome below says which way it moved.
            EntityType = "identity.user_photo", EntityId = subjectUserId.ToString(), Action = AuditAction.Update,
            ActorUserId = actor, DecisionOutcome = "PhotoRemoved",
            Purpose = "staff-directory", Severity = AuditSeverity.Info,
        }, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// The image format the BYTES say they are, or null.
    ///
    /// <para>Three signatures, matching the three the migration's CHECK allows. Deliberately not a general
    /// image parser: the question is not "what is this file" but "is it one of the three things we agreed to
    /// store", and a narrow answer to a narrow question is the one that cannot be talked around.</para>
    /// </summary>
    internal static string? Sniff(ReadOnlySpan<byte> b)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return Png;

        // JPEG: FF D8 FF
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return Jpeg;

        // WebP: "RIFF" .... "WEBP" — the size field between them is why this is not one contiguous compare.
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return Webp;

        return null;
    }

    /// <summary>
    /// Read at most <paramref name="max"/> bytes, or null if the body is longer.
    ///
    /// <para>Reads one byte past the limit deliberately: that extra byte is the difference between "exactly at
    /// the cap" and "over it", and without it a body of precisely `max + 1` would be silently truncated to a
    /// valid-looking image rather than refused.</para>
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// LAYER TWO: the `admin:write` scope, checked in the handler as well as by the group policy.
    ///
    /// <para>The group requires an `admin:*` scope and MFA — which `admin:read` satisfies. Without this, a
    /// read-only administrative token could replace somebody's photograph. `AdminEndpoints` guards its writes
    /// the same way and for the same reason: a control that lives only in a route group is a control that
    /// depends on the group being wired correctly for ever.</para>
    /// </summary>
    private static IResult? RequiresWrite(HttpContext http) =>
        http.User.HasScope("admin:write")
            ? null
            : Results.Problem(statusCode: 403, title: "insufficient-scope", detail: "requires admin:write");

    private static Guid? SubjectOf(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(Claims.Subject), out var id) ? id : null;

    private static string ActorOf(HttpContext http) => http.User.FindFirstValue(Claims.Subject) ?? "admin";
}
