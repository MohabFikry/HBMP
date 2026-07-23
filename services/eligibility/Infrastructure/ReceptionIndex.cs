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
        var like = $"%{term}%";
        var members = await db.Members.AsNoTracking().Where(m =>
                m.MemberNo == term || m.NationalId == term || m.Passport == term
                || m.RefugeeId == term || m.UnhcrNo == term || m.PrimaryPhone == term
                || EF.Functions.ILike(m.GivenName, like) || EF.Functions.ILike(m.FamilyName, like))
            .Take(limit).ToListAsync(ct);

        var results = new List<ReceptionDocument>(members.Count);
        foreach (var m in members)
        {
            var covs = await db.Coverages.AsNoTracking().Where(c => c.BeneficiaryId == m.BeneficiaryId).ToListAsync(ct);
            results.Add(Compose(m, covs));
        }
        return results;
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
            BeneficiaryId = m.BeneficiaryId, MemberNo = m.MemberNo,
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
        bool Match(ReceptionDocument d) =>
            d.MemberNo == term || d.NationalId == term || d.Passport == term || d.RefugeeId == term
            || d.UnhcrNo == term || d.PolicyNo == term || d.PrimaryPhone == term
            || d.GivenName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || d.FamilyName.Contains(term, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<ReceptionDocument>>(_docs.Values.Where(Match).Take(limit).ToList());
    }

    public Task UpsertAsync(ReceptionDocument doc, CancellationToken ct = default)
    {
        _docs[doc.BeneficiaryId] = doc;
        return Task.CompletedTask;
    }
}
