namespace Mersal.Migration.Core;

/// <summary>
/// The reconciliation report every stream must emit: source vs loaded vs rejected, field-mapping
/// coverage, and an exception list with reasons. A migration is NOT "done" until this balances
/// (<see cref="Balances"/>) and exceptions are triaged (phase 12.1 / ../35 §5).
/// </summary>
public sealed class ReconciliationReport(string stream, Guid batchId)
{
    public string Stream { get; } = stream;
    public Guid BatchId { get; } = batchId;

    public int SourceCount { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Rejected { get; set; }

    /// <summary>Rows deliberately NOT loaded pending human action (e.g. dedupe review queue).</summary>
    public int Held { get; set; }

    /// <summary>Per-target-field mapping coverage: field name → how many source rows populated it.</summary>
    public Dictionary<string, int> FieldCoverage { get; } = new(StringComparer.Ordinal);

    /// <summary>Rejected/exception rows: (source id, reason) — the triage list.</summary>
    public List<ReconcileException> Exceptions { get; } = [];

    public int Loaded => Inserted + Updated;

    /// <summary>Reconciliation balances when every source row is accounted for: loaded, held, or rejected.</summary>
    public bool Balances => SourceCount == Loaded + Held + Rejected;

    public void Reject(string sourceId, string reason)
    {
        Rejected++;
        Exceptions.Add(new ReconcileException(sourceId, reason));
    }

    public void CountField(string field)
        => FieldCoverage[field] = FieldCoverage.GetValueOrDefault(field) + 1;

    public override string ToString()
        => $"[{Stream}] source={SourceCount} inserted={Inserted} updated={Updated} held={Held} " +
           $"rejected={Rejected} balances={Balances}";
}

public sealed record ReconcileException(string SourceId, string Reason);

/// <summary>
/// The dedupe report for the beneficiary stream: how each candidate was routed. Low-confidence
/// pairs are NEVER auto-merged — they land in <see cref="QueuedForReview"/> for human sign-off
/// before promotion (phase 12.1 acceptance).
/// </summary>
public sealed class DedupeReport
{
    public List<DedupeOutcome> AutoMerged { get; } = [];
    public List<DedupeOutcome> QueuedForReview { get; } = [];
    public List<DedupeOutcome> NoMatch { get; } = [];

    public void Add(DedupeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        switch (outcome.Decision)
        {
            case MatchDecision.AutoMerge: AutoMerged.Add(outcome); break;
            case MatchDecision.Review: QueuedForReview.Add(outcome); break;
            default: NoMatch.Add(outcome); break;
        }
    }

    public override string ToString()
        => $"dedupe: auto-merged={AutoMerged.Count} queued-for-review={QueuedForReview.Count} " +
           $"no-match={NoMatch.Count}";
}

public sealed record DedupeOutcome(string SourceId, string? MatchedId, double Score, MatchDecision Decision, string Basis);
