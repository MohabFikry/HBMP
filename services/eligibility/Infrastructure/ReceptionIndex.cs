using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Eligibility.Infrastructure;

/// <summary>
/// A reception search hit — the MINIMUM-NECESSARY projection a receptionist may see (11-permission-matrix):
/// identity + coverage/limit summary + a visit-history SUMMARY. It carries NO clinical/EMR data
/// (no diagnoses, notes, orders, prescriptions, results, vitals). This type IS the server-side
/// projection boundary: EMR fields cannot be represented here, so they cannot leak via query manipulation.
/// </summary>
public sealed record ReceptionDocument
{
    public Guid BeneficiaryId { get; init; }
    public string? MemberNo { get; init; }
    /// <summary>The number printed on the card — NOT <see cref="MemberNo"/>. See MemberProjection.</summary>
    public string? CardNumber { get; init; }
    public string GivenName { get; init; } = "";
    public string FamilyName { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public string? NationalId { get; init; }
    public string? Passport { get; init; }
    public string? RefugeeId { get; init; }
    public string? UnhcrNo { get; init; }
    public string? PolicyNo { get; init; }
    public string? PrimaryPhone { get; init; }
    public IReadOnlyList<string> ActiveCategories { get; init; } = [];
    public IReadOnlyList<RemainingLimit> RemainingLimits { get; init; } = [];
    public int VisitCount { get; init; }
    public DateOnly? LastVisitDate { get; init; }
    public string? LastVisitType { get; init; }
}

public sealed record RemainingLimit(string Category, string LimitType, decimal Remaining);

/// <summary>Search + maintain the reception index. Implementations project only min-necessary fields.</summary>
public interface IReceptionIndex
{
    Task<IReadOnlyList<ReceptionDocument>> SearchAsync(string q, int limit, CancellationToken ct = default);

    /// <summary>
    /// Resolve ONE member from something a beneficiary can physically present. Null when nothing matches.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately not <see cref="SearchAsync"/> with a limit of one. That method matches names with
    /// ILIKE and returns whatever the database happened to order first — which is exactly how the eligibility
    /// screen came to check the wrong Ahmed. This one is an EXACT match on a unique identifier: it returns
    /// the member or it returns nothing, and there is no first-of-several to pick.</para>
    ///
    /// <para><b>Both the member number and the card number.</b> They are different identifiers — the first
    /// is the enrolment key policy-service issues, the second is what is printed on the object the
    /// beneficiary hands across the counter — and a desk holding a card must not have to know which is
    /// which.</para>
    ///
    /// <para><b>Phone is not on the list.</b> A household shares one number, so a phone identifies a family
    /// and not a person — the one thing this method exists to do. Nor is a beneficiary GUID: it is a system
    /// key, not something anyone carries to a desk, and admitting it would make "verified against the card
    /// they presented" untrue for that path.</para>
    /// </remarks>
    Task<ReceptionDocument?> FindByPresentedIdentifierAsync(string identifier, CancellationToken ct = default);

    Task UpsertAsync(ReceptionDocument doc, CancellationToken ct = default);
}

/// <summary>
/// Postgres-backed reception search over the min-necessary projections (default backend). Matches by
/// NationalID / Passport / Card(memberNo) / Policy / Phone / name — never touches an EMR table because
/// this schema has none.
/// </summary>
public sealed class PostgresReceptionIndex(EligibilityDbContext db) : IReceptionIndex
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReceptionDocument>> SearchAsync(string q, int limit, CancellationToken ct = default)
    {
        var term = q.Trim();
        var terms = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IQueryable<MemberProjection> query;
        // A bare beneficiary id resolves to that ONE member. Callers that already hold an id and need the card
        // behind it — the call-centre 360 is the live case — had no way to ask: every clause matched a
        // human-facing identifier, so a GUID query returned nothing and the 360 answered 404 forever. It is an
        // exact match on a key the caller already has, so it widens nothing: you cannot discover an id this way,
        // only redeem one.
        if (Guid.TryParse(term, out var beneficiaryId))
        {
            query = db.Members.AsNoTracking().Where(m => m.BeneficiaryId == beneficiaryId);
        }
        else if (terms.Length <= 1)
        {
            var like = $"%{term}%";
            query = db.Members.AsNoTracking().Where(m =>
                m.MemberNo == term || m.CardNumber == term || m.NationalId == term || m.Passport == term
                || m.RefugeeId == term || m.UnhcrNo == term || m.PrimaryPhone == term
                || EF.Functions.ILike(m.GivenName, like) || EF.Functions.ILike(m.FamilyName, like));
        }
        else
        {
            // A multi-word query is a NAME — no identifier or member number contains a space — and the
            // natural way to type one is in full. Matching the whole string against each column separately
            // meant "Omar Khalil" (given "Omar", family "Khalil") returned nothing at the reception desk.
            // Every term must land in one of the name columns.
            query = db.Members.AsNoTracking();
            foreach (var t in terms)
            {
                var like = $"%{t}%";
                query = query.Where(m => EF.Functions.ILike(m.GivenName, like) || EF.Functions.ILike(m.FamilyName, like));
            }
        }
        var members = await query.Take(limit).ToListAsync(ct);

        var results = new List<ReceptionDocument>(members.Count);
        foreach (var m in members)
        {
            var covs = await db.Coverages.AsNoTracking().Where(c => c.BeneficiaryId == m.BeneficiaryId).ToListAsync(ct);
            results.Add(Compose(m, covs));
        }
        return results;
    }

    public async Task<ReceptionDocument?> FindByPresentedIdentifierAsync(string identifier, CancellationToken ct = default)
    {
        var id = identifier.Trim();
        if (id.Length == 0) return null;

        // Equality on every column, no ILIKE and no wildcard: a partial card number must not resolve a member.
        // Take(2) and insist on exactly one, rather than FirstOrDefault. These columns are unique in a clean
        // database and the check costs a row — but if two records ever DO share an identifier, taking whichever
        // the planner returned first is the failure this whole endpoint replaces, and it would reappear here
        // in the one place nobody would look for it.
        var candidates = await db.Members.AsNoTracking().Where(x =>
            x.MemberNo == id || x.CardNumber == id || x.NationalId == id || x.Passport == id
            || x.RefugeeId == id || x.UnhcrNo == id).Take(2).ToListAsync(ct);
        if (candidates.Count > 1) return null;
        var m = candidates.FirstOrDefault();
        if (m is null)
        {
            // The policy number lives on the coverage row rather than the member, and a beneficiary who is
            // handed a policy card has nothing else to type. Resolved through its member so the same card
            // comes back either way.
            var byPolicy = await db.Coverages.AsNoTracking().Where(c => c.PolicyNo == id)
                .Select(c => c.BeneficiaryId).Distinct().Take(2).ToListAsync(ct);
            // A policy covering a whole household is not an individual identifier. Refusing the ambiguous
            // case is the point: silently taking the first is the defect this endpoint replaces.
            if (byPolicy.Count != 1) return null;
            m = await db.Members.AsNoTracking().FirstOrDefaultAsync(x => x.BeneficiaryId == byPolicy[0], ct);
            if (m is null) return null;
        }

        var covs = await db.Coverages.AsNoTracking().Where(c => c.BeneficiaryId == m.BeneficiaryId).ToListAsync(ct);
        return Compose(m, covs);
    }

    public Task UpsertAsync(ReceptionDocument doc, CancellationToken ct = default) => Task.CompletedTask; // reads live

    public static ReceptionDocument Compose(MemberProjection m, IReadOnlyList<CoverageProjection> covs)
    {
        var active = covs.Where(c => string.Equals(c.Status, "Active", StringComparison.OrdinalIgnoreCase)).ToList();
        var remaining = new List<RemainingLimit>();
        foreach (var c in active)
        {
            var limits = JsonSerializer.Deserialize<List<LimitStateDto>>(c.LimitsJson, Json) ?? [];
            foreach (var l in limits)
                remaining.Add(new RemainingLimit(c.BenefitCategory, l.LimitType, l.LimitValue - l.ConsumedValue));
        }
        return new ReceptionDocument
        {
            BeneficiaryId = m.BeneficiaryId, MemberNo = m.MemberNo, CardNumber = m.CardNumber,
            GivenName = m.GivenName, FamilyName = m.FamilyName, Status = m.Status,
            NationalId = m.NationalId, Passport = m.Passport, RefugeeId = m.RefugeeId, UnhcrNo = m.UnhcrNo,
            PolicyNo = active.FirstOrDefault()?.PolicyNo, PrimaryPhone = m.PrimaryPhone,
            ActiveCategories = active.Select(c => c.BenefitCategory).Distinct().ToList(),
            RemainingLimits = remaining,
            VisitCount = 0, LastVisitDate = null, LastVisitType = null, // fed by EncounterStarted (phase 2.3+)
        };
    }
}

/// <summary>In-memory index for tests / single-node dev without a search backend.</summary>
public sealed class InMemoryReceptionIndex : IReceptionIndex
{
    private readonly ConcurrentDictionary<Guid, ReceptionDocument> _docs = new();

    public Task<IReadOnlyList<ReceptionDocument>> SearchAsync(string q, int limit, CancellationToken ct = default)
    {
        var term = q.Trim();
        // Mirrors the DB index: an id redeems to its own card.
        if (Guid.TryParse(term, out var byId))
            return Task.FromResult<IReadOnlyList<ReceptionDocument>>(
                _docs.TryGetValue(byId, out var only) ? [only] : []);
        bool Match(ReceptionDocument d) =>
            d.MemberNo == term || d.CardNumber == term || d.NationalId == term || d.Passport == term || d.RefugeeId == term
            || d.UnhcrNo == term || d.PolicyNo == term || d.PrimaryPhone == term
            || d.GivenName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || d.FamilyName.Contains(term, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<ReceptionDocument>>(_docs.Values.Where(Match).Take(limit).ToList());
    }

    public Task<ReceptionDocument?> FindByPresentedIdentifierAsync(string identifier, CancellationToken ct = default)
    {
        var id = identifier.Trim();
        if (id.Length == 0) return Task.FromResult<ReceptionDocument?>(null);
        bool Exact(ReceptionDocument d) =>
            d.MemberNo == id || d.CardNumber == id || d.NationalId == id || d.Passport == id
            || d.RefugeeId == id || d.UnhcrNo == id;
        var hit = _docs.Values.Where(Exact).Take(2).ToList();
        if (hit.Count == 1) return Task.FromResult<ReceptionDocument?>(hit[0]);
        if (hit.Count > 1) return Task.FromResult<ReceptionDocument?>(null);
        // Policy last, and only when it names exactly one person — see the interface.
        var byPolicy = _docs.Values.Where(d => d.PolicyNo == id).Take(2).ToList();
        return Task.FromResult(byPolicy.Count == 1 ? byPolicy[0] : null);
    }

    public Task UpsertAsync(ReceptionDocument doc, CancellationToken ct = default)
    {
        _docs[doc.BeneficiaryId] = doc;
        return Task.CompletedTask;
    }
}
