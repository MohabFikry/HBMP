using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.MasterData.Api;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHbmpAuthentication(builder.Configuration);
builder.Services.AddHbmpAuditClient("masterdata-service");
builder.Services.AddDbContext<MasterDataDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("MasterData")
                ?? throw new System.InvalidOperationException("Database connection string is not configured — inject it via ConnectionStrings env/OpenBao; never a baked credential.")).UseSnakeCaseNamingConvention());
builder.Services.AddHbmpEvents(builder.Configuration);
builder.Services.AddHbmpDurableOutbox<MasterDataDbContext>();
builder.Services.AddHbmpOutboxRelay();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("masterdata-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Readiness for the probe in infra/helm/rollout/rollout-template.yaml. Process-level only: this reports
// "through startup and able to serve". A dependency check here would pull the pod out of rotation for a
// condition the service already surfaces per-request, turning a partial degradation into a total outage.
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseHbmpTransportSecurity(); // HSTS + HTTPS redirect outside Development (16.5, H8)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "masterdata-service" })).AllowAnonymous();
// Without this the readinessProbe 404s and the canary rollout waits forever on a healthy pod. Anonymous
// because kubelet carries no bearer token.
app.MapHealthChecks("/health/ready").AllowAnonymous();

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
    await db.IcdCodes.FindAsync([code], ct) is { } e ? Results.Ok(e) : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

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
    await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == drugCode, ct) is { } d ? Results.Ok(d) : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

v1.MapGet("/allergens", async (MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Allergens.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)));

// 14.6 — examination types (filter by category / sensitivity) + a single fetch orders uses to pin sensitivity.
v1.MapGet("/examination-types", async (string? category, string? sensitivity, MasterDataDbContext db, CancellationToken ct) =>
{
    var q = db.ExaminationTypes.AsNoTracking().Where(x => x.Status == "Active");
    if (Enum.TryParse<ExamCategory>(category, out var c)) q = q.Where(x => x.Category == c);
    if (Enum.TryParse<SensitivityLevel>(sensitivity, out var s)) q = q.Where(x => x.SensitivityLevel == s);
    var rows = await q.OrderBy(x => x.NameEn).ToListAsync(ct);
    return Results.Ok(rows.Select(ExamView.Of));
});

v1.MapGet("/examination-types/{id:guid}", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
{
    var x = await db.ExaminationTypes.AsNoTracking().FirstOrDefaultAsync(e => e.ExaminationTypeId == id && e.Status == "Active", ct);
    return x is null ? Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found") : Results.Ok(ExamView.Of(x));
});

// ------------------------------------------------------------------ Typeahead search (Tier 1: DB ILIKE; OpenSearch indexer is a follow-up)
v1.MapGet("/search", async (string domain, string q, MasterDataDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q)) return ProblemResults.Invalid("q-required");
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
        : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

// "Is this thing a medicine?" — asked by inventory-service before it will admit an item to the CLINIC STOCK
// catalogue (ADR-0029 D5: vaccines and anything dispensed against a prescription are pharmacy stock, not
// clinic stock).
//
// ANSWERED HERE, not in inventory, and that is the point. The medicines master lives in this service; a copy
// of "what counts as a medicine" kept in a storekeeping service is a second answer to a clinical question,
// and the two would drift the first time a drug was added here and not there.
//
// Reference data only, both ways: the caller sends a SKU and a name and gets back a verdict plus the matched
// drug's public catalogue fields. No beneficiary, no prescription, nothing patient-shaped crosses this call.
v1.MapGet("/drugs/classify", async (string? code, string? name, string? nameAr, MasterDataDbContext db, CancellationToken ct) =>
{
    var c = code?.Trim();
    var n = name?.Trim();
    var na = nameAr?.Trim();
    if (string.IsNullOrWhiteSpace(c) && string.IsNullOrWhiteSpace(n) && string.IsNullOrWhiteSpace(na))
        return Results.Problem(statusCode: 400, title: "code, name or nameAr is required",
            type: "https://mersal.foundation/problems/validation");

    // The containment arm — "Hepatitis B Vaccine 20mcg/ml" must match the master's "Hepatitis B Vaccine" —
    // is what makes this catch the real mistake rather than only the exactly-typed one. It is floored at
    // SIX characters of drug name because without a floor a master entry called "Water" refuses every
    // consumable with "water" in its description, and a guard that fires on gauze gets switched off.
    const int MinContainmentLength = 6;

    var hit = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(d =>
           (c != null && EF.Functions.ILike(d.DrugCode, c))
        || (n != null && EF.Functions.ILike(d.Name, n))
        || (na != null && d.NameAr != null && EF.Functions.ILike(d.NameAr, na))
        || (n != null && d.Name.Length >= MinContainmentLength && EF.Functions.ILike(n, "%" + d.Name + "%"))
        || (n != null && d.ScientificName != null && d.ScientificName.Length >= MinContainmentLength
            && EF.Functions.ILike(n, "%" + d.ScientificName + "%"))
        || (na != null && d.NameAr != null && d.NameAr.Length >= MinContainmentLength
            && EF.Functions.ILike(na, "%" + d.NameAr + "%")), ct);

    return Results.Ok(new
    {
        matched = hit is not null,
        drugCode = hit?.DrugCode,
        name = hit?.Name,
        atcCode = hit?.AtcCode,
        // ATC J07 is the vaccines group. Reported separately because a vaccine landing in clinic stock is the
        // specific case D5 was raised about, and the refusal reads better when it can say so.
        isVaccine = hit?.AtcCode is { } atc && atc.StartsWith("J07", StringComparison.OrdinalIgnoreCase),
    });
});

// By-id existence checks the EMR uses to validate drug_id / allergen_id references (phase 4 medication
// history & allergies). Return allow/deny only — no clinical payload.
v1.MapGet("/drugs/by-id/{id:guid}/exists", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(new { id, exists = await db.Drugs.AsNoTracking().AnyAsync(x => x.DrugId == id, ct) }));
v1.MapGet("/allergens/{id:guid}/exists", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(new { id, exists = await db.Allergens.AsNoTracking().AnyAsync(x => x.AllergenId == id, ct) }));

// Policy-approved alternatives for a drug (phase 6.3 formulary): the other drugs in the SAME ATC-5 class
// (same therapeutic substance) — a clinically-sound generic-substitution set from real master data. Returns
// both the id list (consumed by the pharmacy formulary service) and drug details (for the pharmacist UI).
v1.MapGet("/drugs/by-id/{id:guid}/alternatives", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
{
    var self = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugId == id, ct);
    if (self is null || string.IsNullOrWhiteSpace(self.AtcCode))
        return Results.Ok(new { alternatives = Array.Empty<Guid>(), drugs = Array.Empty<object>() });
    var alts = await db.Drugs.AsNoTracking()
        .Where(x => x.AtcCode == self.AtcCode && x.DrugId != id)
        .OrderBy(x => x.Name).Take(8).ToListAsync(ct);
    return Results.Ok(new
    {
        alternatives = alts.Select(x => x.DrugId),
        drugs = alts.Select(x => new { x.DrugId, x.Name, x.NameAr, x.AtcCode, x.Form, x.Strength }),
    });
});

// Highest-severity interaction among a set of drug codes (order-insensitive).
v1.MapPost("/drug-interactions/check", async (DrugCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var drugs = await db.Drugs.AsNoTracking().Where(d => req.DrugCodes.Contains(d.DrugCode))
        .Select(d => new { d.DrugId, d.DrugCode }).ToListAsync(ct);
    var ids = drugs.Select(d => d.DrugId).ToHashSet();
    var hits = await db.DrugInteractions.AsNoTracking()
        .Where(i => ids.Contains(i.DrugAId) && ids.Contains(i.DrugBId))
        .ToListAsync(ct);
    var top = hits.Count == 0 ? (InteractionSeverity?)null : hits.Max(h => h.Severity);
    await Screen(audit, me, "drug-interaction-screening", $"{{\"drugCount\":{drugs.Count},\"highestSeverity\":\"{top}\"}}", ct);
    return Results.Ok(new
    {
        checkedDrugs = drugs.Select(d => d.DrugCode),
        interactions = hits.Select(h => new { severity = h.Severity.ToString(), h.Description }),
        highestSeverity = top?.ToString(),
    });
});

// By-id interaction check the pharmacy uses (prescription_line.drug_id are uuids). Highest-severity interaction
// among a set of drug ids (order-insensitive).
v1.MapPost("/drug-interactions/check-by-ids", async (DrugIdCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var hits = await db.DrugInteractions.AsNoTracking()
        .Where(i => ids.Contains(i.DrugAId) && ids.Contains(i.DrugBId))
        .ToListAsync(ct);
    var top = hits.Count == 0 ? (InteractionSeverity?)null : hits.Max(h => h.Severity);
    await Screen(audit, me, "drug-interaction-screening", $"{{\"drugCount\":{ids.Count},\"highestSeverity\":\"{top}\"}}", ct);
    return Results.Ok(new
    {
        checkedDrugIds = ids,
        interactions = hits.Select(h => new { severity = h.Severity.ToString(), h.DrugAId, h.DrugBId, h.Description }),
        highestSeverity = top?.ToString(),
    });
});

// By-id allergy check the pharmacy uses: flag a drug (uuid) against a beneficiary's allergen ids (uuids). A
// conflict is raised when the drug's ATC code (or an ancestor) matches a Drug-category allergen's code.
v1.MapPost("/allergies/check-by-ids", async (AllergyIdCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var drug = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugId == req.DrugId, ct);
    if (drug is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    var atcChain = drug.AtcCode is null
        ? new HashSet<string>()
        : MasterDataNormalize.AtcAncestors(drug.AtcCode).Append(drug.AtcCode).ToHashSet(StringComparer.Ordinal);
    var allergenIds = (req.AllergenIds ?? []).ToHashSet();
    var codes = await db.Allergens.AsNoTracking()
        .Where(a => allergenIds.Contains(a.AllergenId)).Select(a => a.Code).ToListAsync(ct);
    var conflict = codes.Any(c => atcChain.Contains(c));
    await Screen(audit, me, "allergy-screening", $"{{\"allergenCount\":{allergenIds.Count},\"conflict\":{conflict.ToString().ToLowerInvariant()}}}", ct);
    return Results.Ok(new { req.DrugId, conflict, matchedOn = conflict ? "atc-class" : null });
});

// Flag a drug against a patient's allergen codes/classes.
v1.MapPost("/allergies/check", async (AllergyCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var drug = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == req.DrugCode, ct);
    if (drug is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");
    // Conflict when the drug's ATC code (or an ancestor) matches a patient drug-allergen code.
    var atcChain = drug.AtcCode is null
        ? new HashSet<string>()
        : MasterDataNormalize.AtcAncestors(drug.AtcCode).Append(drug.AtcCode).ToHashSet(StringComparer.Ordinal);
    var conflict = req.PatientAllergenCodes.Any(a => atcChain.Contains(a));
    await Screen(audit, me, "allergy-screening", $"{{\"allergenCount\":{req.PatientAllergenCodes.Length},\"conflict\":{conflict.ToString().ToLowerInvariant()}}}", ct);
    return Results.Ok(new { req.DrugCode, conflict, matchedOn = conflict ? "atc-class" : null });
});

app.MapPrometheusScrapingEndpoint(); // /metrics — golden signals (Phase 11.3)

// 16.6 (H2/audit): every clinical-decision-support screening records who screened + a de-identified
// summary (counts + outcome, never the patient's drug/allergen values) as an audited Read.
static async Task Screen(IAuditClient audit, IHbmpPrincipalAccessor me, string entityType, string summary, CancellationToken ct) =>
    await audit.EmitAsync(new AuditEventDraft
    {
        EntityType = entityType,
        EntityId = "screening",
        Action = AuditAction.Read,
        ActorUserId = me.Principal?.Subject,
        FieldClasses = ["clinical"],
        AfterState = summary,
    }, ct);

app.Run();

public partial class Program;
