using System.Text.Json;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.5b — saved extract definitions and their runs (design 38 §4.4).
///
/// <para>Small extracts stream back inline; large ones are stored and downloaded through document-service's
/// authorized, audited endpoint. Either way the run row records the filter that was executed, the columns
/// granted, the columns WITHHELD and the row count — the four facts a later review of "what left the
/// building" needs and cannot reconstruct from anything else.</para>
/// </summary>
public static class ExtractEndpoints
{
    public static void MapExtracts(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1/extracts").RequireAuthorization(HbmpPolicies.Scope("policy:read"));
        var write = app.MapGroup("/api/v1/extracts").RequireAuthorization(HbmpPolicies.Scope("policy:read"));

        MapColumns(read);
        MapDefinitions(read, write);
        MapRuns(read, write);
    }

    // ---- The column catalogue ----------------------------------------------------------------------------

    private static void MapColumns(RouteGroupBuilder read)
    {
        // What this caller may ask for, per entity. Published so the UI builds a column picker from the same
        // list the server enforces, rather than from a hard-coded copy that drifts.
        read.MapGet("/columns", async (string? entity, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            var capabilities = Capabilities(principal.Roles);

            var entities = entity is null
                ? Enum.GetValues<ExtractEntity>()
                : Enum.TryParse<ExtractEntity>(entity, ignoreCase: true, out var e) ? [e] : [];
            if (entities.Length == 0)
                return ProblemResults.Invalid("UNKNOWN_ENTITY", $"'{entity}' is not an extract entity.");

            return Results.Ok(entities.Select(en => new
            {
                entity = en.ToString(),
                columns = ExtractColumns.For(en).Select(c => new
                {
                    name = c.Name,
                    @class = c.Class.ToString(),
                    // Availability is computed, not implied. A picker that greys out what the caller cannot
                    // have is honest; one that hides it makes the rule invisible and unarguable.
                    available = c.Class != ExtractColumnClass.Clinical && capabilities.Allows(c.Class),
                }),
                @default = ExtractColumns.DefaultFor(en),
            }));
        });
    }

    // ---- Definitions -------------------------------------------------------------------------------------

    private static void MapDefinitions(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        write.MapPost("/definitions", async (
            SaveExtractDefinition req, PolicyDbContext db, PolicyGate gate, TimeProvider clock, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
                return ProblemResults.Invalid("NAME_REQUIRED", "A definition needs a name.");
            if (!Enum.TryParse<ExtractEntity>(req.Entity, ignoreCase: true, out var entity))
                return ProblemResults.Invalid("UNKNOWN_ENTITY", $"'{req.Entity}' is not an extract entity.");
            var format = Enum.TryParse<ExtractFormat>(req.Format, ignoreCase: true, out var f) ? f : ExtractFormat.Csv;

            // A schedule is validated at DEFINITION time, against a grammar this service can actually evaluate.
            // Accepting an expression we cannot run would store a nightly file that never arrives, discovered
            // months later by whoever was waiting for it.
            if (!string.IsNullOrWhiteSpace(req.ScheduleCron))
            {
                if (!ExtractSchedule.TryParse(req.ScheduleCron, out _))
                    return ProblemResults.Invalid("UNSUPPORTED_SCHEDULE",
                        "Supported schedules are @daily, @weekly, @monthly, or 'm h * * *'. " +
                        "An expression this service cannot evaluate is refused rather than stored and never run.");
                if (req.ServiceScopePayerIds is null || req.ServiceScopePayerIds.Count == 0)
                    return ProblemResults.Unprocessable("SCHEDULE_SCOPE_REQUIRED",
                        "A scheduled extract runs under a service principal and must name the payers it may read. " +
                        "An empty scope is not 'unrestricted' — it is unconfigured, and would be a nightly file " +
                        "containing every payer's membership.");
            }

            var now = clock.GetUtcNow();
            var definition = new ExtractDefinition
            {
                DefinitionId = Guid.NewGuid(), Name = req.Name.Trim(), Description = req.Description,
                Entity = entity, Filter = JsonSerializer.Serialize(req.Filter ?? new ExtractFilter()),
                Columns = JsonSerializer.Serialize(req.Columns ?? []), Format = format,
                OwnerUserId = gate.SubjectId, IsShared = req.IsShared, ScheduleCron = req.ScheduleCron,
                ServiceScopePayerIds = req.ServiceScopePayerIds is { Count: > 0 } ids ? string.Join(',', ids) : null,
                CreatedAt = now, UpdatedAt = now, CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
            };
            db.ExtractDefinitions.Add(definition);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                return ProblemResults.Conflict("DUPLICATE_NAME", "An extract with that name already exists.");
            }

            return Results.Created($"/api/v1/extracts/definitions/{definition.DefinitionId}",
                ExtractDefinitionView.From(definition));
        })
        .Produces<ExtractDefinitionView>();

        read.MapGet("/definitions", async (PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var mine = gate.SubjectId;
            var rows = await db.ExtractDefinitions.AsNoTracking()
                .Where(d => !d.IsDeleted && (d.IsShared || d.OwnerUserId == mine))
                .OrderBy(d => d.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(ExtractDefinitionView.From));
        });
    }

    // ---- Runs --------------------------------------------------------------------------------------------

    private static void MapRuns(RouteGroupBuilder read, RouteGroupBuilder write)
    {
        write.MapPost("/run", async (
            RunExtract req, ExtractEngine engine, PolicyDbContext db, PolicyGate gate,
            IPayerDirectory payers, HttpContext http, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            if (req is null) return ProblemResults.Invalid("BODY_REQUIRED", "A run needs an entity or a definition.");

            ExtractEntity entity;
            var filter = req.Filter ?? new ExtractFilter();
            var columns = req.Columns;
            var format = Enum.TryParse<ExtractFormat>(req.Format, ignoreCase: true, out var f) ? f : ExtractFormat.Csv;

            if (req.DefinitionId is { } definitionId)
            {
                var definition = await db.ExtractDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DefinitionId == definitionId && !d.IsDeleted, ct);
                if (definition is null)
                    return ProblemResults.NotFound("DEFINITION_NOT_FOUND", "No such extract definition.");
                entity = definition.Entity;
                filter = req.Filter ?? JsonSerializer.Deserialize<ExtractFilter>(definition.Filter) ?? new ExtractFilter();
                columns ??= JsonSerializer.Deserialize<List<string>>(definition.Columns);
                if (req.Format is null) format = definition.Format;
            }
            else if (!Enum.TryParse(req.Entity, ignoreCase: true, out entity))
            {
                return ProblemResults.Invalid("UNKNOWN_ENTITY", $"'{req.Entity}' is not an extract entity.");
            }

            var permitted = await payers.GetAsync(principal, ct);
            // Naming a payer outside your scope is 403 rather than an empty file — the same call ADR-0024 makes
            // for the queries, and for the same reason: an empty file reads as "no members", which is a fact
            // about the payer rather than about the caller's permissions.
            if (filter.PayerId is { } requestedPayer && !permitted.Allows(requestedPayer))
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to extract this payer's data.", reason: "payer-not-permitted");

            var result = await engine.RunAsync(
                new ExtractRequest(entity, filter, columns, format, req.DefinitionId),
                Capabilities(principal.Roles), permitted,
                new ActorRef(gate.SubjectId, gate.Subject), gate.Subject,
                http.Request.Headers.Authorization.FirstOrDefault(), isScheduled: false, ct);

            if (result.Refusal is not null)
                return ProblemResults.Unprocessable("EXTRACT_REFUSED", result.Refusal,
                    new Dictionary<string, object?> { ["withheld"] = result.Withheld });

            // Inline for a small file. The withheld columns ride in a HEADER rather than only in the run row,
            // because the person who downloads a CSV never sees the JSON that would otherwise carry them.
            if (result.Inline is not null)
            {
                if (result.Withheld.Count > 0)
                    http.Response.Headers["X-Withheld-Columns"] = string.Join(',', result.Withheld.Select(w => w.Name));
                return Results.File(result.Inline, result.ContentType, result.FileName);
            }

            return Results.Ok(new ExtractRunView(
                result.Run!.RunId, result.Run.Entity.ToString(), result.RowCount, result.Columns,
                result.Withheld, result.DocumentId,
                result.DocumentId is null ? null : $"/api/v1/operational-documents/{result.DocumentId}/content",
                result.Run.AsOf, result.Run.Status.ToString(), result.Run.StartedAt, result.Run.CompletedAt));
        })
        .Produces<ExtractRunView>();

        read.MapGet("/runs", async (Guid? definitionId, int? page, int? pageSize,
            PolicyDbContext db, PolicyGate gate, CancellationToken ct) =>
        {
            if (await gate.CheckAsync(PolicyPolicies.Read, ct) is { } denied) return denied;
            var pageReq = PageRequest.Of(page, pageSize);
            var query = db.ExtractRuns.AsNoTracking().AsQueryable();
            if (definitionId is { } id) query = query.Where(r => r.DefinitionId == id);

            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(r => r.StartedAt)
                .Skip(pageReq.Skip).Take(pageReq.PageSize).ToListAsync(ct);
            return Results.Ok(new
            {
                page = pageReq.Page, pageSize = pageReq.PageSize, total,
                items = rows.Select(r => new ExtractRunView(
                    r.RunId, r.Entity.ToString(), r.RowCount,
                    JsonSerializer.Deserialize<List<string>>(r.ColumnSnapshot) ?? [],
                    r.WithheldSnapshot is null ? [] : JsonSerializer.Deserialize<List<WithheldColumn>>(r.WithheldSnapshot) ?? [],
                    r.FileDocumentId,
                    r.FileDocumentId is null ? null : $"/api/v1/operational-documents/{r.FileDocumentId}/content",
                    r.AsOf, r.Status.ToString(), r.StartedAt, r.CompletedAt)),
            });
        })
        .Produces<IEnumerable<ExtractRunView>>();
    }

    /// <summary>The caller's column capabilities, taken from the SAME role lists 19.5's projections use — so a
    /// column an officer cannot see on screen is not one they can extract into a spreadsheet.</summary>
    private static ExtractCapabilities Capabilities(IReadOnlyCollection<string> roles) => new(
        Amounts: AdministrativeProjection.MayReadAmounts(roles),
        Contract: AdministrativeProjection.MayReadContract(roles),
        Case: AdministrativeProjection.MayReadCase(roles),
        // Names come from patient-service, and whoever may read a member list there may read them here.
        Identity: AdministrativeProjection.MayReadCase(roles));
}

public sealed record SaveExtractDefinition(
    string Name, string Entity, string? Description = null, ExtractFilter? Filter = null,
    IReadOnlyList<string>? Columns = null, string? Format = null, bool IsShared = false,
    string? ScheduleCron = null, IReadOnlyList<Guid>? ServiceScopePayerIds = null);

public sealed record RunExtract(
    string? Entity = null, Guid? DefinitionId = null, ExtractFilter? Filter = null,
    IReadOnlyList<string>? Columns = null, string? Format = null);

public sealed record ExtractDefinitionView(
    Guid DefinitionId, string Name, string? Description, string Entity, string Format,
    bool IsShared, string? ScheduleCron, bool ScheduleRunnable, DateTimeOffset CreatedAt)
{
    public static ExtractDefinitionView From(ExtractDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new ExtractDefinitionView(
            d.DefinitionId, d.Name, d.Description, d.Entity.ToString(), d.Format.ToString(),
            d.IsShared, d.ScheduleCron,
            // Surfaced explicitly: a definition with a schedule and no service scope exists but will not run,
            // and the list is where somebody notices that before the file fails to arrive.
            d.ScheduleCron is not null && !string.IsNullOrWhiteSpace(d.ServiceScopePayerIds),
            d.CreatedAt);
    }
}

public sealed record ExtractRunView(
    Guid RunId, string Entity, int RowCount, IReadOnlyList<string> Columns,
    IReadOnlyList<WithheldColumn> Withheld, Guid? DocumentId, string? DownloadPath,
    DateOnly? AsOf, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
