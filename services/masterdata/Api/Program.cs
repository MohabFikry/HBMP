using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Events;
using Mersal.MasterData.Api;
using Mersal.MasterData.Domain;
using Mersal.MasterData.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
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

// Reads are public reference data, but the endpoint is still bounded (phase 26.1).
//
// This replaces a bare RequireAuthorization(). The earlier position — recorded in MasterDataAuthzTests —
// was that a scope everyone holds is a control in name only, and that remains true of *humans*: nearly every
// clinical role is granted masterdata:read, and rightly so. The scope earns its place elsewhere. It makes
// reference-data reach an explicit, revocable line in the role matrix rather than an implicit consequence of
// holding any token at all, and it means a service or integration token must ASK for the catalogue instead
// of receiving it by default. An unscoped endpoint is an unbounded one.
var v1 = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope(MasterDataScopes.Read));

static int Clamp(int? n, int dflt, int max) => Math.Clamp(n ?? dflt, 1, max);

// ------------------------------------------------------------------ ICD-10
//
// ============================================================================================================
// THE LIST IS TWO COLUMNS: CODE AND DESCRIPTION
// ============================================================================================================
// `Master Lists/ICD10_2019_WHO_Full.xlsx` has nine columns, and the loader stores four of them. Only two are
// the LIST — the code and what it means. The rest are structure the catalogue needs to do its job and nobody
// reads off a screen: `chapter` and `is_billable` stay stored and stay filterable below, they are simply not
// part of what a caller is handed. A projection rather than a migration, because dropping `is_billable` from
// the table would also drop the only thing that distinguishes a diagnosis from a chapter heading — see the
// typeahead's filter further down, which depends on it.
//
// The filters are deliberately kept while the fields they filter on leave the response. `?chapter=` and
// `?billable=` are how a caller asks a QUESTION of the catalogue; returning the answer's internals is a
// separate thing, and every consumer of these rows (emr's code validator, the portal's title lookup) already
// reads nothing but the code and the title.
v1.MapGet("/icd-codes", async (string? chapter, bool? billable, string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 50, 200));
    var query = db.IcdCodes.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(chapter)) query = query.Where(x => x.Chapter == chapter);
    if (billable is not null) query = query.Where(x => x.IsBillable == billable);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => EF.Functions.ILike(x.Code, $"{q}%") || EF.Functions.ILike(x.Title, $"%{q}%"));
    var items = await query.OrderBy(x => x.Code).Skip((p - 1) * ps).Take(ps)
        .Select(x => new { x.Code, x.Title }).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = ps, items });
});
v1.MapGet("/icd-codes/{code}", async (string code, MasterDataDbContext db, CancellationToken ct) =>
    await db.IcdCodes.FindAsync([code], ct) is { } e
        ? Results.Ok(new { e.Code, e.Title })
        : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

// ------------------------------------------------------------------ CPT
//
// ============================================================================================================
// `section` IS NOT `category`, AND ONLY ONE OF THEM ANSWERS "IS THIS A SCAN OR A BLOOD TEST"
// ============================================================================================================
// The stored `category` is the CPT taxonomy — Category I / II / III / PLA / MAAA — which is how a code was
// adopted into the book. The SECTION is the code's numeric range, which is what the Labs and Imaging tabs
// are actually asking about. `CptSections` owns those ranges and the evidence for them; this endpoint only
// applies them. It takes a COMMA-SEPARATED list because the two are not one-to-one: the Imaging tab is one
// section, and the Labs tab is Laboratory plus Pathology — a specimen sent to a pathologist and a sample run
// on an analyser are both ordered from the same tab and are not the same section.
//
// ------------------------------------------------------------------------------------------------------
// WHAT COMES BACK, AND IN WHAT ORDER
// ------------------------------------------------------------------------------------------------------
// Two columns, code and description — the same rule as ICD above, and for the same reason: `category` and
// `sourceRelease` rode out on every keystroke of a typeahead that displays neither.
//
// The ORDER is the part that decides whether a 20-row page is useful. Both fields are always searched, but
// which kind of match leads depends on what the doctor typed: a query beginning with a digit is somebody
// reading a code off a request form, so code matches come first; anything else is somebody describing what
// they want, so description matches come first. Ranking rather than filtering, because the other kind of
// match is still a match — "80048" typed into a description-led sort would rank a panel that merely mentions
// the number above the code itself.
v1.MapGet("/cpt-codes", async (string? category, string? section, string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 50, 200));
    var query = db.CptCodes.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
    if (CptSections.PatternFor(section) is { } pattern)
        query = query.Where(x => Regex.IsMatch(x.Code, pattern));

    var needle = q?.Trim() ?? "";
    IOrderedQueryable<CptCode> ordered;
    if (needle.Length == 0)
    {
        ordered = query.OrderBy(x => x.Code);
    }
    else
    {
        // ILike on BOTH halves. The code half was `StartsWith`, which Postgres compiles to a case-SENSITIVE
        // `LIKE` — and CPT codes are not all digits: the Category II/III/PLA/MAAA codes end in F/T/U/M, so
        // "0001f" found nothing while "0001F" found the row.
        query = query.Where(x => EF.Functions.ILike(x.Code, $"{needle}%") || EF.Functions.ILike(x.Description, $"%{needle}%"));
        ordered = char.IsDigit(needle[0])
            ? query.OrderBy(x => EF.Functions.ILike(x.Code, $"{needle}%") ? 0 : 1).ThenBy(x => x.Code)
            : query.OrderBy(x => EF.Functions.ILike(x.Description, $"%{needle}%") ? 0 : 1).ThenBy(x => x.Code);
    }

    var items = await ordered.Skip((p - 1) * ps).Take(ps)
        .Select(x => new { x.Code, x.Description }).ToListAsync(ct);
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
// 26.2 — the prescribing combobox. One field over trade name, active ingredient and Arabic name, because a
// prescriber thinks in whichever they know: "augmentin" and "amoxicillin" must both reach the same product.
// Declared before /drugs/{drugCode} for legibility; routing prefers the literal segment either way.
v1.MapGet("/drugs/search", async (string? q, int? page, int? pageSize, MasterDataDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < DrugSearch.MinQueryLength)
    {
        return Results.Problem(
            statusCode: 400, title: "Query too short",
            detail: $"'q' must be at least {DrugSearch.MinQueryLength} characters.",
            type: "https://mersal.foundation/problems/validation");
    }

    var (p, ps) = (Clamp(page, 1, int.MaxValue), Clamp(pageSize, 20, DrugSearch.MaxPageSize));
    var items = await DrugSearch.SearchAsync(db, q, p, ps, ct);
    return Results.Ok(new { page = p, pageSize = ps, items });
});

v1.MapGet("/drugs/{drugCode}", async (string drugCode, MasterDataDbContext db, CancellationToken ct) =>
    await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == drugCode, ct) is { } d ? Results.Ok(d) : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

v1.MapGet("/allergens", async (MasterDataDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Allergens.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)));

// One allergen by id. emr-service calls this when RECORDING an allergy: it needs the name, not just an
// existence bit, so the clinical record can say which substance rather than storing a uuid nobody can read
// (emr migration 0020). `/allergens/{id}/exists` stays for callers that genuinely only ask allow/deny.
v1.MapGet("/allergens/{id:guid}", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    await db.Allergens.AsNoTracking().FirstOrDefaultAsync(x => x.AllergenId == id, ct) is { } a
        ? Results.Ok(a)
        : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

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
        // Both halves case-insensitive, and the CODE half is the one that was not.
        //
        // `StartsWith` translates to `LIKE 'q%'`, which in Postgres is case-SENSITIVE — so the title matched
        // "sinusitis" and "Sinusitis" alike while the code matched "J06" and refused "j06". A clinician typing
        // a code in lower case (which is how people type) got "No ICD-10 code matches that search" for a code
        // that is in the catalogue, and the search box says "by code or condition". Half a promise.
        //
        // The asymmetry is the tell that this was an oversight rather than a decision: nobody chooses to make
        // one of two fields in the same query behave differently. Note also that it could not be caught by the
        // portal's own tests — the dev fixture client lower-cases both sides, so the demo has always been
        // case-insensitive and only the live catalogue was not.
        //
        // ------------------------------------------------------------------------------------------------
        // GROUPING ROWS ARE NOT DIAGNOSES
        // ------------------------------------------------------------------------------------------------
        // `is_billable` is false exactly for the source sheet's `chapter` and `block` rows — "A00-A09
        // Intestinal infectious diseases" is a range heading in the classification, not a condition anyone
        // has. They were reaching the doctor's diagnosis field alongside real codes and nothing marked them
        // apart, so a range could be staged as the visit's PRIMARY diagnosis: the code the authorization, the
        // claim and the formulary check all key on. Downstream, `A00-A09` normalizes to the category `A00`
        // and would silently be read as cholera.
        //
        // The filter belongs here, on the typeahead, rather than on `/icd-codes` above: this endpoint exists
        // to answer "which diagnosis do you mean", while the listing is the catalogue itself and a catalogue
        // legitimately contains its own hierarchy. A caller who wants the headings asks `?billable=false`.
        //
        // Two columns out, in the same breath: `Chapter` went with it. No caller ever read it — the portal
        // maps code and title and drops the rest — so it was a field travelling on every typeahead keystroke
        // to be discarded on arrival.
        "icd" => await db.IcdCodes.AsNoTracking()
            .Where(x => x.IsBillable && (EF.Functions.ILike(x.Code, $"{q}%") || EF.Functions.ILike(x.Title, $"%{q}%")))
            .OrderBy(x => x.Code).Take(20).Select(x => new { x.Code, x.Title }).ToListAsync(ct),
        // Same two-column rule and same case-insensitivity as `/cpt-codes` above, which is the endpoint the
        // ordering screens actually use. Kept in step deliberately: two search paths over one catalogue that
        // disagree about what a match is are a bug waiting for whichever caller moves between them.
        "cpt" => await db.CptCodes.AsNoTracking().Where(x => EF.Functions.ILike(x.Code, $"{q}%") || EF.Functions.ILike(x.Description, $"%{q}%"))
            .OrderBy(x => x.Code).Take(20).Select(x => new { x.Code, x.Description }).ToListAsync(ct),
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
// One drug by id. pharmacy-service calls this when a prescription is SUBMITTED: it needs the product's name,
// not just an existence bit, so the prescription can say which medicine rather than storing a uuid a
// pharmacist cannot read (pharmacy migration 0006). `/drugs/by-id/{id}/exists` stays for callers that
// genuinely only ask allow/deny.
v1.MapGet("/drugs/by-id/{id:guid}", async (Guid id, MasterDataDbContext db, CancellationToken ct) =>
    await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugId == id, ct) is { } d
        ? Results.Ok(d)
        : Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found"));

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
    // The by-CODE sibling of the route below, for callers holding catalogue codes rather than uuids. It runs
    // the SAME matcher: two implementations would diverge, and the way this pair would diverge is one route
    // reassuring where the other warns.
    var drugs = await db.Drugs.AsNoTracking().Where(d => req.DrugCodes.Contains(d.DrugCode)).ToListAsync(ct);

    var compositions = new List<DrugComposition>(drugs.Count);
    foreach (var drug in drugs) compositions.Add(await CompositionAsync(db, drug, ct));

    var rules = await db.Set<InteractionRule>().AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);
    var hits = InteractionMatcher.Match(compositions, rules);
    var top = hits.Count == 0 ? (InteractionSeverity?)null : hits.Max(h => h.Rule.Severity);

    await Screen(audit, me, "drug-interaction-screening", $"{{\"drugCount\":{drugs.Count},\"highestSeverity\":\"{top}\"}}", ct);
    return Results.Ok(new
    {
        checkedDrugs = drugs.Select(d => d.DrugCode),
        interactions = hits.Select(h => new
        {
            severity = h.Rule.Severity.ToString(),
            h.MatchedOn,
            mechanismEn = h.Rule.MechanismEn,
            clinicalEffectEn = h.Rule.ClinicalEffectEn,
            managementEn = h.Rule.ManagementEn,
            h.Rule.Citation,
        }),
        highestSeverity = top?.ToString(),
    });
});

// By-id interaction check the pharmacy uses (prescription_line.drug_id are uuids). Highest-severity interaction
// among a set of drug ids (order-insensitive).
v1.MapPost("/drug-interactions/check-by-ids", async (DrugIdCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    /*
     * 28.3 — INGREDIENT-LEVEL. This read `masterdata.drug_interaction`, which keys a pair on two PRODUCT
     * uuids. The catalogue holds 22,653 products, so one clinical fact — warfarin interacts with NSAIDs —
     * needed a row for every pair of BRANDS containing them. The table has zero rows and would have stayed
     * empty: it was not a data backlog, it was an unpopulatable model (doc 44 §1.2).
     *
     * Each product is now resolved to its molecules and its ATC ancestor chain, and the curated rules are
     * matched across the pairs. One row — warfarin × M01A — covers every brand of each, in both directions,
     * and keeps covering them as the market changes.
     */
    var ids = (req.DrugIds ?? []).ToHashSet();

    var drugs = await db.Drugs.AsNoTracking().Where(d => ids.Contains(d.DrugId)).ToListAsync(ct);
    var compositions = new List<DrugComposition>(drugs.Count);
    foreach (var drug in drugs) compositions.Add(await CompositionAsync(db, drug, ct));

    // Active rules only. An unreviewed rule has no named pharmacist behind it and is not permitted to warn
    // a prescriber — enforced by ck_interaction_rule_reviewed, and applied here as well so a constraint
    // change can never silently widen what fires.
    var rules = await db.Set<InteractionRule>().AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);
    var hits = InteractionMatcher.Match(compositions, rules);

    var top = hits.Count == 0 ? (InteractionSeverity?)null : hits.Max(h => h.Rule.Severity);

    // How many rules the list holds AT ALL. Without it the caller cannot tell "no interaction between these
    // medicines" from "the list is empty, so nothing could have been found". The second is not a clean
    // result and must report "not checked" — internal curation means partial coverage by construction, and
    // the UI has to state the extent rather than imply completeness (doc 43 D3, doc 44 §8).
    var knownPairCount = await db.Set<InteractionRule>().AsNoTracking().CountAsync(r => r.IsActive, ct);
    var updatedAt = await db.Set<InteractionRule>().AsNoTracking()
        .Where(r => r.IsActive).MaxAsync(r => (DateTimeOffset?)r.UpdatedAt, ct);

    // A product with no molecules AND no ATC code cannot be matched against any rule. Reported so the engine
    // can say which medicine it could not check rather than implying the whole prescription was screened.
    var unresolvable = compositions.Where(c => !c.IsResolvable).Select(c => c.DrugId).ToList();

    await Screen(audit, me, "drug-interaction-screening",
        $"{{\"drugCount\":{ids.Count},\"highestSeverity\":\"{top}\",\"rules\":{knownPairCount}}}", ct);

    return Results.Ok(new
    {
        checkedDrugIds = ids,
        interactions = hits.Select(h => new
        {
            severity = h.Rule.Severity.ToString(),
            drugAId = h.DrugAId,
            drugBId = h.DrugBId,
            h.MatchedOn,
            // Design 44 §3: mechanism, consequence and action. "Major interaction with clarithromycin" is
            // not something a prescriber can act on; "suspend the statin for the antibiotic course" is.
            mechanismEn = h.Rule.MechanismEn, mechanismAr = h.Rule.MechanismAr,
            clinicalEffectEn = h.Rule.ClinicalEffectEn, clinicalEffectAr = h.Rule.ClinicalEffectAr,
            managementEn = h.Rule.ManagementEn, managementAr = h.Rule.ManagementAr,
            onset = h.Rule.Onset.ToString(),
            evidenceLevel = h.Rule.EvidenceLevel.ToString(),
            h.Rule.Citation,
        }),
        highestSeverity = top?.ToString(),
        knownPairCount,
        rulesUpdatedAt = updatedAt,
        unresolvableDrugIds = unresolvable,
        sourceRelease = rules.Select(r => r.SourceRelease).FirstOrDefault(),
    });
});

// 26.3 — the drug↔indication link, for the indication↔diagnosis check. Codes are 3-character ICD
// CATEGORIES; the caller compares at category level (see masterdata.search_key's sibling rationale in
// 0006_drug_indication.sql). A drug that returns an EMPTY list is not a mismatch — it is a drug with no
// indication data, which must report "not checked" rather than "no match" or, worse, "OK".
// List prices for a set of catalogue products, for the dispensing counter's cost-share quote.
//
// Separate from the ingredients route rather than bolted onto it: a price and an active ingredient are
// different facts with different audiences — one is money, the other is clinical — and they are read by
// different callers for different reasons. Folding both into one payload would mean every clinical caller
// pulled prices it has no use for.
v1.MapPost("/drugs/prices/by-ids", async (DrugIdCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var rows = await db.Drugs.AsNoTracking()
        .Where(d => ids.Contains(d.DrugId))
        .Select(d => new { d.DrugId, d.Name, d.PriceEgp })
        .ToListAsync(ct);

    return Results.Ok(new
    {
        // Every id is answered for, and an unpriced product returns NULL rather than 0. The distinction is
        // the whole point at a counter: 0 is "this medicine is free", null is "we do not know what it costs",
        // and quoting the first when the second is true is how a member is told the wrong figure.
        items = ids.Select(id => new
        {
            drugId = id,
            name = rows.FirstOrDefault(r => r.DrugId == id)?.Name,
            priceEgp = rows.FirstOrDefault(r => r.DrugId == id)?.PriceEgp,
        }),
        currency = "EGP",
    });
});

// List prices for a set of examinations, for the lab / imaging bench's cost quote (ADR-0034). The exact
// counterpart of the drug price route above, and it answers the same way for the same reason.
//
// Keyed on CODE, not on the examination-type id: an order line always carries a code (CPT 71046, LOINC
// 58410-2) and only carries an examination_type_id if it was written after phase 14.6. A price lookup that
// silently returned nothing for every pre-14.6 line would read as "these examinations are free".
//
// A code matches either the catalogue's own short code (CXR) or its default billing code (71046) — the
// order line records the latter, the catalogue is browsed by the former.
v1.MapPost("/examination-types/prices/by-codes", async (ExamPriceRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var codes = (req.Codes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var rows = await db.ExaminationTypes.AsNoTracking()
        .Where(x => x.Status == "Active" && (codes.Contains(x.Code) || (x.DefaultCode != null && codes.Contains(x.DefaultCode))))
        .Select(x => new { x.Code, x.DefaultCode, x.NameEn, x.PriceEgp })
        .ToListAsync(ct);

    return Results.Ok(new
    {
        // Every code is answered for, and an unpriced examination returns NULL rather than 0 — the same
        // distinction the drug route draws, and the same consequence if it is blurred: 0 at a bench means
        // "this scan is free", null means "we do not know what it costs".
        items = codes.Select(code =>
        {
            var row = rows.FirstOrDefault(r =>
                string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.DefaultCode, code, StringComparison.OrdinalIgnoreCase));
            return new { code, name = row?.NameEn, priceEgp = row?.PriceEgp };
        }),
        currency = "EGP",
    });
});

// Active ingredients for a set of catalogue products. Catalogue data only — no patient context is accepted
// or returned. The prescribing path uses it to look manufacturer labels up by ingredient, so that the drug
// ids stay inside the platform and only a molecule name is sent to the external label source.
v1.MapPost("/drugs/ingredients/by-ids", async (DrugIdCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var rows = await db.Drugs.AsNoTracking()
        .Where(d => ids.Contains(d.DrugId))
        .Select(d => new { d.DrugId, d.Name, d.ScientificName, d.AtcCode })
        .ToListAsync(ct);

    // 28.5 — the resolved MOLECULES alongside the free-text scientific name. Duplicate therapy is a question
    // about molecules ("both of these contain paracetamol"), and the commonest real duplication is two trade
    // names holding the same one — which no amount of comparing product ids or trade names will find.
    var composition = await db.Set<DrugIngredient>().AsNoTracking()
        .Where(x => ids.Contains(x.DrugId))
        .Select(x => new { x.DrugId, x.IngredientKey })
        .ToListAsync(ct);

    return Results.Ok(new
    {
        // Every id asked about is answered for, including the ones with no ingredient recorded — 2,786
        // products are in that state, and "not recorded" is the answer that makes the check say so rather
        // than quietly omit the line.
        items = ids.Select(id =>
        {
            var row = rows.FirstOrDefault(r => r.DrugId == id);
            return new
            {
                drugId = id,
                // The name MASTER DATA holds, never one the client sent — the same rule the prescription-create
                // path enforces, and for the same reason: a client-supplied label would let the medicine named in
                // a safety warning differ from the drug actually prescribed.
                name = row?.Name,
                scientificName = row?.ScientificName,
                atcCode = row?.AtcCode,
                ingredientKeys = composition.Where(c => c.DrugId == id).Select(c => c.IngredientKey).Order(StringComparer.Ordinal),
            };
        }),
    });
});

/*
 * 28.7 — the ancestors of a set of ICD codes, so the indication check can walk the hierarchy instead of
 * truncating to three characters (design 44 §6).
 *
 * Truncation works for the common case — a drug indicated at category level, a diagnosis coded at
 * subcategory level — and breaks in three ways it cannot see: a BLOCK-level indication ("J00-J06") is
 * inexpressible, an indication more specific than three characters is silently widened to its whole
 * category, and a diagnosis LESS specific than the indication reads as a mismatch when it is an open
 * question.
 *
 * Every code asked about is answered for, including ones with no hierarchy row. An empty ancestor list is a
 * real answer — the catalogue has not been reloaded since the closure arrived — and the engine falls back to
 * the category comparison rather than reporting a mismatch on every prescription.
 */
/*
 * 28.9 — drug–disease contraindications (design 44 §5).
 *
 * The check the request actually wanted. Indication MISMATCH is off-label and mostly noise; this is harm.
 * The condition side walks the ICD hierarchy, so a rule at K27 catches K27.9 without enumerating it, and
 * pregnancy is answered from the patient's recorded STATUS rather than from a code on this visit — a rule
 * keyed on O00-O9A would fire only for the patient nobody needs reminding about.
 */
/*
 * 28.10 — the dosing rule that applies to THIS patient for THIS indication (design 44 §4).
 *
 * The fetcher this replaces was `_ = drugIds; _ = ct;` returning an empty dictionary, provenance
 * "not-yet-configured". Selection is most-specific-first: an indication scope beats the general ceiling, and
 * a rule naming the patient's population beats one that does not. Taking the strictest of every matching
 * rule would sound safer and would apply a paediatric mg/kg ceiling to an adult.
 */
v1.MapPost("/dosing-rules/by-ids", async (DosingRuleRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var codes = (req.DiagnosisIcdCodes ?? [])
        .Where(c => !string.IsNullOrWhiteSpace(c)).Select(MasterDataNormalize.Icd)
        .ToHashSet(StringComparer.Ordinal);

    var ancestorRows = await db.Set<IcdAncestor>().AsNoTracking()
        .Where(a => codes.Contains(a.Code)).Select(a => new { a.Code, a.AncestorCode }).ToListAsync(ct);
    var ancestors = codes.ToDictionary(
        c => c,
        c => (IReadOnlyList<string>)ancestorRows.Where(r => r.Code == c).Select(r => r.AncestorCode).ToList(),
        StringComparer.Ordinal);

    var population = Enum.TryParse<DosingPopulation>(req.Population, out var p) ? p : (DosingPopulation?)null;
    var rules = await db.Set<DosingRule>().AsNoTracking().Where(r => r.IsActive).ToListAsync(ct);
    var drugs = await db.Drugs.AsNoTracking().Where(d => ids.Contains(d.DrugId)).ToListAsync(ct);

    var items = new List<object>();
    foreach (var drug in drugs)
    {
        var rule = DosingRuleSelector.Select(
            await CompositionAsync(db, drug, ct), ancestors, population, req.Route, rules);
        if (rule is null) continue;

        items.Add(new
        {
            drugId = drug.DrugId,
            // The ceiling for THIS patient: mg/kg × weight where the rule is weight-based, capped at the
            // adult maximum. Computed here, where the cap rule lives, rather than left to each caller.
            maxDailyDose = DosingRuleSelector.MaxDailyFor(rule, req.WeightKg),
            rule.DoseUnit,
            rule.MaxDurationDays,
            rule.IsWeightBased,
            rule.RequiresRenalFunction,
            typicalDailyDose = rule.TypicalDaily,
            population = rule.Population.ToString(),
            rule.Citation,
        });
    }

    return Results.Ok(new { items, knownRuleCount = rules.Count });
});

v1.MapPost("/contraindications/check-by-ids", async (ContraindicationCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var codes = (req.DiagnosisIcdCodes ?? [])
        .Where(c => !string.IsNullOrWhiteSpace(c)).Select(MasterDataNormalize.Icd)
        .ToHashSet(StringComparer.Ordinal);

    var ancestorRows = await db.Set<IcdAncestor>().AsNoTracking()
        .Where(a => codes.Contains(a.Code)).Select(a => new { a.Code, a.AncestorCode }).ToListAsync(ct);
    var ancestors = codes.ToDictionary(
        c => c,
        c => (IReadOnlyList<string>)ancestorRows.Where(r => r.Code == c).Select(r => r.AncestorCode).ToList(),
        StringComparer.Ordinal);

    var rules = await db.Set<DrugDiseaseContraindication>().AsNoTracking()
        .Where(r => r.IsActive).ToListAsync(ct);

    var drugs = await db.Drugs.AsNoTracking().Where(d => ids.Contains(d.DrugId)).ToListAsync(ct);

    var hits = new List<ContraindicationHit>();
    foreach (var drug in drugs)
    {
        hits.AddRange(ContraindicationMatcher.Match(
            await CompositionAsync(db, drug, ct), ancestors, req.IsPregnant, rules));
    }

    await Screen(audit, me, "contraindication-screening",
        $"{{\"drugCount\":{ids.Count},\"diagnosisCount\":{codes.Count},\"hits\":{hits.Count}}}", ct);

    return Results.Ok(new
    {
        items = hits.Select(h => new
        {
            drugId = h.DrugId,
            severity = h.Rule.Severity.ToString(),
            h.MatchedOn,
            icdScope = h.Rule.IcdScope,
            mechanismEn = h.Rule.MechanismEn, mechanismAr = h.Rule.MechanismAr,
            clinicalEffectEn = h.Rule.ClinicalEffectEn, clinicalEffectAr = h.Rule.ClinicalEffectAr,
            managementEn = h.Rule.ManagementEn, managementAr = h.Rule.ManagementAr,
            evidenceLevel = h.Rule.EvidenceLevel.ToString(),
            h.Rule.Citation,
        }),
        // The same coverage honesty the interaction list carries: "no contraindication found" against eight
        // curated rules is a weaker statement than the same words against a licensed database.
        knownRuleCount = rules.Count,
    });
});

v1.MapPost("/icd-codes/ancestors", async (IcdCodeListRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var codes = (req.Codes ?? [])
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(MasterDataNormalize.Icd)
        .ToHashSet(StringComparer.Ordinal);

    var rows = await db.Set<IcdAncestor>().AsNoTracking()
        .Where(a => codes.Contains(a.Code))
        .OrderBy(a => a.Depth)
        .Select(a => new { a.Code, a.AncestorCode })
        .ToListAsync(ct);

    return Results.Ok(new
    {
        items = codes.Select(code => new
        {
            code,
            ancestors = rows.Where(r => r.Code == code).Select(r => r.AncestorCode),
        }),
    });
});

v1.MapPost("/drug-indications/by-ids", async (DrugIdCheckRequest req, MasterDataDbContext db, CancellationToken ct) =>
{
    var ids = (req.DrugIds ?? []).ToHashSet();
    var rows = await db.DrugIndications.AsNoTracking()
        .Where(i => ids.Contains(i.DrugId) && i.DeletedAt == null)
        .Select(i => new { i.DrugId, i.IcdCode, i.Source, i.SourceRelease })
        .ToListAsync(ct);

    return Results.Ok(new
    {
        items = ids.Select(id => new
        {
            drugId = id,
            icdCategories = rows.Where(r => r.DrugId == id).Select(r => r.IcdCode).Distinct().OrderBy(c => c, StringComparer.Ordinal),
        }),
        // Per-row provenance from the source workbook's own "ICD Basis" column, surfaced so the prescriber
        // can weigh the advice. The mapping is generated at ATC level 4 and is clinical judgement, not a
        // published dataset — doc 43 §1 rule 2.
        source = rows.Select(r => r.Source).FirstOrDefault(),
        sourceRelease = rows.Select(r => r.SourceRelease).FirstOrDefault(),
    });
});

/*
 * By-id allergy check the pharmacy uses: flag a drug (uuid) against a beneficiary's allergen ids (uuids).
 *
 * THIS ENDPOINT USED TO BE INCAPABLE OF EVER RAISING A CONFLICT (doc 44 §1.1). It built the drug's ATC
 * ancestor chain — J, J01, J01C, J01CA — and tested whether any recorded allergen CODE appeared in it. The
 * seeded codes are ALG-PENICILLIN, ALG-SULFA, ALG-CEPHALO (0002_seed_allergens.sql). The two sets are
 * disjoint by construction, so `codes.Any(c => atcChain.Contains(c))` was a constant false, and the
 * prescribing engine rendered that as "no conflict with the 3 recorded allergies".
 *
 * The response therefore no longer answers with a bare boolean. A caller cannot tell a screen that ran and
 * found nothing from a screen that could not run at all, and the engine's whole five-state model depends on
 * being able to. It now reports THREE facts:
 *
 *   conflict              — did a mapped allergen match this medicine
 *   screenedAllergenCount — how many of the supplied allergens were actually compared
 *   unmappedAllergens     — the DRUG allergens we hold no mapping for, by display name
 *
 * Food and environmental allergens are neither screened nor unmapped: a peanut allergy is not a question
 * about a medicine, and counting it as a coverage gap would make every patient look like one.
 *
 * Phase 28.1.1 ships the honesty before the mapping. Until the allergen→ingredient tables land in 28.1.2
 * there is no mapping for ANY drug allergen, so every one of them is reported unmapped and the engine
 * reports NotChecked naming them. That is exactly true, and it is what removes the false assurance today.
 */
v1.MapPost("/allergies/check-by-ids", async (AllergyIdCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var drug = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugId == req.DrugId, ct);
    if (drug is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var allergenIds = (req.AllergenIds ?? []).ToHashSet();
    var composition = await CompositionAsync(db, drug, ct);
    var mappings = await MappingsAsync(db, allergenIds, ct);

    var screen = AllergyMatcher.Screen(composition, mappings);
    var hit = screen.Strongest;

    await Screen(audit, me, "allergy-screening",
        $"{{\"allergenCount\":{allergenIds.Count},\"screened\":{screen.ScreenedAllergenCount},"
        + $"\"unmapped\":{screen.UnmappedAllergens.Count},"
        + $"\"conflict\":{(hit is not null).ToString().ToLowerInvariant()}}}", ct);

    return Results.Ok(new
    {
        req.DrugId,
        conflict = hit is not null,
        matchedOn = hit?.MatchedOn,
        matchKind = hit?.Kind.ToString(),
        // Only a cross-reaction has one. Null means the match IS the recorded allergy rather than an
        // inference from it, and the prescribing engine words the finding differently for each.
        confidence = hit?.Confidence?.ToString(),
        statementEn = hit?.StatementEn,
        statementAr = hit?.StatementAr,
        citation = hit?.Citation,
        screenedAllergenCount = screen.ScreenedAllergenCount,
        unmappedAllergens = screen.UnmappedAllergens,
        // A medicine with neither a recorded ingredient nor an ATC code cannot be compared with ANY allergy.
        // Reported as a property of the drug so the prescriber is not told their patient's allergy record is
        // at fault when the catalogue row is.
        drugResolvable = screen.DrugResolvable,
    });
});

/// <summary>
/// What one medicine is made of: its molecules, and its ATC code with every ancestor.
/// </summary>
/// <remarks>
/// Both, not either. <c>drug_ingredient</c> decomposes combination products — which is what lets a
/// co-amoxiclav brand screen as amoxicillin — and the ATC chain expresses "any penicillin" for the products
/// whose ingredient text the catalogue never recorded. Falling back from one to the other is what keeps the
/// check working across a catalogue where 4.7% of rows have no ingredient and 14.8% have no ATC.
/// </remarks>
static async Task<DrugComposition> CompositionAsync(MasterDataDbContext db, Drug drug, CancellationToken ct)
{
    var ingredients = await db.Set<DrugIngredient>().AsNoTracking()
        .Where(x => x.DrugId == drug.DrugId).Select(x => x.IngredientKey).ToListAsync(ct);

    var atcChain = drug.AtcCode is null
        ? new HashSet<string>(StringComparer.Ordinal)
        : MasterDataNormalize.AtcAncestors(drug.AtcCode).Append(MasterDataNormalize.Atc(drug.AtcCode))
            .ToHashSet(StringComparer.Ordinal);

    return new DrugComposition(drug.DrugId, ingredients.ToHashSet(StringComparer.Ordinal), atcChain);
}

/// <summary>Every recorded allergen with what it maps to — molecules, ATC scopes and cross-reactivity.</summary>
static async Task<List<AllergenMapping>> MappingsAsync(
    MasterDataDbContext db, HashSet<Guid> allergenIds, CancellationToken ct)
{
    if (allergenIds.Count == 0) return [];

    var allergens = await db.Allergens.AsNoTracking()
        .Where(a => allergenIds.Contains(a.AllergenId)).ToListAsync(ct);

    var exact = await db.Set<AllergenIngredient>().AsNoTracking()
        .Where(x => allergenIds.Contains(x.AllergenId)).ToListAsync(ct);

    var links = await db.Set<AllergenCrossReactivity>().AsNoTracking()
        .Where(x => allergenIds.Contains(x.AllergenId)).ToListAsync(ct);

    var groupCodes = links.Select(l => l.GroupCode).Distinct().ToList();
    var groups = await db.Set<CrossReactivityGroup>().AsNoTracking()
        .Where(g => groupCodes.Contains(g.GroupCode)).ToListAsync(ct);
    var members = await db.Set<CrossReactivityMember>().AsNoTracking()
        .Where(m => groupCodes.Contains(m.GroupCode)).ToListAsync(ct);

    var rules = groups.ToDictionary(g => g.GroupCode, g => new CrossReactivityRule(
        g.GroupCode, g.NameEn, g.Confidence, g.StatementEn, g.StatementAr, g.Citation,
        members.Where(m => m.GroupCode == g.GroupCode && m.IngredientKey != null)
            .Select(m => m.IngredientKey!).ToHashSet(StringComparer.Ordinal),
        members.Where(m => m.GroupCode == g.GroupCode && m.AtcScope != null)
            .Select(m => MasterDataNormalize.Atc(m.AtcScope!)).ToHashSet(StringComparer.Ordinal)),
        StringComparer.Ordinal);

    return [.. allergens.Select(a => new AllergenMapping(
        a.AllergenId, a.Name,
        // Category is the source of truth for "could a medicine ever match this", and the column is the
        // curated override. A Drug-category allergen someone marked unmappable stays unmappable.
        a.IsDrugMappable && a.Category == AllergenCategory.Drug,
        exact.Where(x => x.AllergenId == a.AllergenId).Select(x => x.IngredientKey).ToHashSet(StringComparer.Ordinal),
        (a.AtcScopes ?? []).Select(MasterDataNormalize.Atc).ToHashSet(StringComparer.Ordinal),
        [.. links.Where(l => l.AllergenId == a.AllergenId)
            .Select(l => rules.TryGetValue(l.GroupCode, out var r) ? r : null)
            .Where(r => r is not null).Select(r => r!)]))];
}

// Flag a drug against a patient's allergen CODES. The by-ids route above is the one the prescribing path
// calls; this one is keyed on the catalogue's own codes for callers that hold those instead.
//
// It carried the identical defect and is corrected identically — an allergen code was tested against the
// drug's ATC ancestor chain, which is a comparison between two disjoint code spaces. It reports the same
// three facts as the by-ids route, for the same reason: a bare boolean cannot distinguish "screened, clean"
// from "could not screen", and a caller that cannot tell them apart will render the second as the first.
v1.MapPost("/allergies/check", async (AllergyCheckRequest req, MasterDataDbContext db, IAuditClient audit, IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var drug = await db.Drugs.AsNoTracking().FirstOrDefaultAsync(x => x.DrugCode == req.DrugCode, ct);
    if (drug is null) return Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    var codes = (req.PatientAllergenCodes ?? []).ToHashSet(StringComparer.Ordinal);
    var allergenIds = await db.Allergens.AsNoTracking()
        .Where(a => codes.Contains(a.Code)).Select(a => a.AllergenId).ToListAsync(ct);

    // The SAME matcher as the by-ids route. Two implementations of a matching rule diverge, and the way
    // this one would diverge is one route reassuring where the other warns.
    var screen = AllergyMatcher.Screen(
        await CompositionAsync(db, drug, ct),
        await MappingsAsync(db, allergenIds.ToHashSet(), ct));
    var hit = screen.Strongest;

    await Screen(audit, me, "allergy-screening",
        $"{{\"allergenCount\":{codes.Count},\"screened\":{screen.ScreenedAllergenCount},"
        + $"\"unmapped\":{screen.UnmappedAllergens.Count},"
        + $"\"conflict\":{(hit is not null).ToString().ToLowerInvariant()}}}", ct);

    return Results.Ok(new
    {
        req.DrugCode,
        conflict = hit is not null,
        matchedOn = hit?.MatchedOn,
        matchKind = hit?.Kind.ToString(),
        confidence = hit?.Confidence?.ToString(),
        statementEn = hit?.StatementEn,
        statementAr = hit?.StatementAr,
        citation = hit?.Citation,
        screenedAllergenCount = screen.ScreenedAllergenCount,
        unmappedAllergens = screen.UnmappedAllergens,
        drugResolvable = screen.DrugResolvable,
    });
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
