using Mersal.Auth;
using Mersal.MasterData.Api;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddDbContext<MasterDataDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("MasterData")
                ?? "Host=postgres;Database=hbmp;Username=hbmp;Password=hbmp").UseSnakeCaseNamingConvention());

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("masterdata-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "masterdata-service" })).AllowAnonymous();

// Reads are Public reference data but still require an authenticated caller (observed + authorized).
var v1 = app.MapGroup("/api/v1").RequireAuthorization();

static int Clamp(int? n, int dflt, int max) => Math.Clamp(n ?? dflt, 1, max);

// ------------------------------------------------------------------ ICD-10
v1.MapGet("/icd-codes", async (string? chapter, bool? billable, string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 50, 200));
    var query = db.IcdCodes.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(chapter)) query = query.Where(x => x.Chapter == chapter);
    if (billable is not null) query = query.Where(x => x.IsBillable == billable);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Code.StartsWith(q) || EF.Functions.ILike(x.Title, $"%{q}%"));
    var items = await query.OrderBy(x => x.Code).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = ps, items });
});
v1.MapGet("/icd-codes/{code}", async (string code, MasterDataDbContext db, CancellationToken ct) =>
    await db.IcdCodes.FindAsync([code], ct) is { } e ? Results.Ok(e) : Results.NotFound());

// ------------------------------------------------------------------ CPT
v1.MapGet("/cpt-codes", async (string? category, string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 50, 200));
    var query = db.CptCodes.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Code.StartsWith(q) || EF.Functions.ILike(x.Description, $"%{q}%"));
    var items = await query.OrderBy(x => x.Code).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = ps, items });
});

// ------------------------------------------------------------------ ATC + drugs
v1.MapGet("/atc-classes", async (int? level, string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 50, 200));
    var query = db.AtcClasses.AsNoTracking();
    if (level is not null) query = query.Where(x => x.Level == level);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.AtcCode.StartsWith(q) || EF.Functions.ILike(x.Title, $"%{q}%"));
    return Results.Ok(await query.OrderBy(x => x.AtcCode).Skip((p - 1) * ps).Take(ps).ToListAsync(ct));
});
v1.MapGet("/drugs", async (string? atcCode, string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 50, 200));
    var query = db.Drugs.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(atcCode)) query = query.Where(x => x.AtcCode == atcCode);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => EF.Functions.ILike(x.Name, $"%{q}%") || x.DrugCode.StartsWith(q));
    var items = await query.OrderBy(x => x.Name).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = ps, items });
});
v1.MapGet("/drugs/{drugCode}", async (string drugCode, MasterDataDbContext db, CancellationToken ct) =>
    await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == drugCode, ct) is { } d ? Results.Ok(d) : Results.NotFound());

v1.MapGet("/allergens", async (MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Allergens.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)));

// ------------------------------------------------------------------ Typeahead search (Tier 1: DB ILIKE; OpenSearch indexer is a follow-up)
v1.MapGet("/search", async (string domain, string q, MasterDataDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q required" });
    object results = domain.ToLowerInvariant() switch
    {
        "icd" => await db.IcdCodes.AsNoTracking().Where(x => x.Code.StartsWith(q) || EF.Functions.ILike(x.Title, $"%{q}%"))
            .OrderBy(x => x.Code).Take(20).Select(x => new { x.Code, x.Title, x.Chapter }).ToListAsync(ct),
        "cpt" => await db.CptCodes.AsNoTracking().Where(x => x.Code.StartsWith(q) || EF.Functions.ILike(x.Description, $"%{q}%"))
            .OrderBy(x => x.Code).Take(20).Select(x => new { x.Code, x.Description, x.Category }).ToListAsync(ct),
        "drug" => await db.Drugs.AsNoTracking().Where(x => EF.Functions.ILike(x.Name, $"%{q}%"))
            .OrderBy(x => x.Name).Take(20).Select(x => new { x.DrugCode, x.Name, x.AtcCode }).ToListAsync(ct),
        _ => Array.Empty<object>(),
    };
    return Results.Ok(results);
});

// ================================================================== VALIDATION ENDPOINTS (0b.3)
// The stable contracts EMR / orders / prescriptions call (phase 4/5/6). Allow/deny + reason.

v1.MapGet("/icd-codes/{code}/exists", async (string code, MasterDataDbContext db, CancellationToken ct) =>
{
    var norm = MasterDataNormalize.Icd(code);
    return Results.Ok(new { code = norm, exists = await db.IcdCodes.AsNoTracking().AnyAsync(x => x.Code == norm, ct) });
});
v1.MapGet("/cpt-codes/{code}/exists", async (string code, MasterDataDbContext db, CancellationToken ct) =>
{
    var norm = MasterDataNormalize.Cpt(code);
    return Results.Ok(new { code = norm, exists = await db.CptCodes.AsNoTracking().AnyAsync(x => x.Code == norm, ct) });
});
v1.MapGet("/drugs/resolve", async (string code, MasterDataDbContext db, CancellationToken ct) =>
    await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == code, ct) is { } d
        ? Results.Ok(new { d.DrugCode, d.Name, d.Form, d.Strength, d.AtcCode })
        : Results.NotFound(new { code, resolved = false }));

// By-id existence checks the EMR uses to validate drug_id / allergen_id references (phase 4 medication
// history & allergies). Return allow/deny only — no clinical payload.
v1.MapGet("/drugs/by-id/{id:guid}/exists", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(new { id, exists = await db.Drugs.AsNoTracking().AnyAsync(x => x.DrugId == id, ct) }));
v1.MapGet("/allergens/{id:guid}/exists", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(new { id, exists = await db.Allergens.AsNoTracking().AnyAsync(x => x.AllergenId == id, ct) }));

// Highest-severity interaction among a set of drug codes (order-insensitive).
v1.MapPost("/drug-interactions/check", async (DrugCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var drugs = await db.Drugs.AsNoTracking().Where(d => req.DrugCodes.Contains(d.DrugCode))
        .Select(d => new { d.DrugId, d.DrugCode }).ToListAsync(ct);
    var ids = drugs.Select(d => d.DrugId).ToHashSet();
    var hits = await db.DrugInteractions.AsNoTracking()
        .Where(i => ids.Contains(i.DrugAId) && ids.Contains(i.DrugBId))
        .ToListAsync(ct);
    var top = hits.Count == 0 ? (InteractionSeverity?)null : hits.Max(h => h.Severity);
    return Results.Ok(new
    {
        checkedDrugs = drugs.Select(d => d.DrugCode),
        interactions = hits.Select(h => new { severity = h.Severity.ToString(), h.Description }),
        highestSeverity = top?.ToString(),
    });
});

// By-id interaction check the pharmacy uses (prescription_line.drug_id are uuids). Highest-severity interaction
// among a set of drug ids (order-insensitive).
v1.MapPost("/drug-interactions/check-by-ids", async (DrugIdCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var hits = await db.DrugInteractions.AsNoTracking()
        .Where(i => ids.Contains(i.DrugAId) && ids.Contains(i.DrugBId))
        .ToListAsync(ct);
    var top = hits.Count == 0 ? (InteractionSeverity?)null : hits.Max(h => h.Severity);
    return Results.Ok(new
    {
        checkedDrugIds = ids,
        interactions = hits.Select(h => new { severity = h.Severity.ToString(), h.DrugAId, h.DrugBId, h.Description }),
        highestSeverity = top?.ToString(),
    });
});

// By-id allergy check the pharmacy uses: flag a drug (uuid) against a beneficiary's allergen ids (uuids). A
// conflict is raised when the drug's ATC code (or an ancestor) matches a Drug-category allergen's code.
v1.MapPost("/allergies/check-by-ids", async (AllergyIdCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var drug = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugId == req.DrugId, ct);
    if (drug is null) return Results.NotFound(new { req.DrugId, resolved = false });
    var atcChain = drug.AtcCode is null
        ? new HashSet<string>()
        : MasterDataNormalize.AtcAncestors(drug.AtcCode).Append(drug.AtcCode).ToHashSet(StringComparer.Ordinal);
    var allergenIds = (req.AllergenIds ?? []).ToHashSet();
    var codes = await db.Allergens.AsNoTracking()
        .Where(a => allergenIds.Contains(a.AllergenId)).Select(a => a.Code).ToListAsync(ct);
    var conflict = codes.Any(c => atcChain.Contains(c));
    return Results.Ok(new { req.DrugId, conflict, matchedOn = conflict ? "atc-class" : null });
});

// Flag a drug against a patient's allergen codes/classes.
v1.MapPost("/allergies/check", async (AllergyCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var drug = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == req.DrugCode, ct);
    if (drug is null) return Results.NotFound(new { req.DrugCode, resolved = false });
    // Conflict when the drug's ATC code (or an ancestor) matches a patient drug-allergen code.
    var atcChain = drug.AtcCode is null
        ? new HashSet<string>()
        : MasterDataNormalize.AtcAncestors(drug.AtcCode).Append(drug.AtcCode).ToHashSet(StringComparer.Ordinal);
    var conflict = req.PatientAllergenCodes.Any(a => atcChain.Contains(a));
    return Results.Ok(new { req.DrugCode, conflict, matchedOn = conflict ? "atc-class" : null });
});

app.Run();

public partial class Program;
