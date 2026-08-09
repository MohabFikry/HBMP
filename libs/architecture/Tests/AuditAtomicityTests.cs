using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// INV-AUDIT-SURVIVES-CRASH — a mutation and its audit record commit together.
/// </summary>
/// <remarks>
/// <para>CLAUDE.md: "Every create/update/state-change/decision/consume/dispense/export/PHI-read writes an
/// immutable, append-only, hash-chained <c>audit_event</c>." <c>IAuditClient.EmitAsync</c> is durable — it
/// stages the event in the service's own outbox — but <c>EfOutbox.EnqueueRawAsync</c> calls its own
/// <c>SaveChangesAsync</c>. So a handler that commits its business transaction and THEN emits has two
/// separate commits with a window between them. A process kill in that window leaves the state changed and
/// NO audit record, forever: nothing anywhere records that one was owed, so no relay, retry or replay will
/// ever produce it.</para>
/// <para>That is worse than the outbox case it mirrors. A lost domain event is a projection that falls
/// behind and can be rebuilt from the source of truth. A lost audit event is a hole in the hash chain's
/// coverage — a decision, a PHI read or a dispense that the platform cannot prove happened, discovered by
/// whoever is trying to answer for it.</para>
/// <para><b>The rule.</b> Within one handler, an <c>EmitAsync</c> must not appear after a
/// <c>CommitAsync</c>. Moving the emit above the commit is the whole fix and costs nothing: the audit event
/// goes to the same outbox, in the same transaction, and the relay delivers it exactly as before.</para>
/// <para><b>What is deliberately NOT flagged.</b> An emit in a handler with no transaction at all — a PHI-read
/// audit, a refusal, a 404 path. There is nothing there to be atomic with, and demanding a transaction around
/// a read would be ceremony. The rule fires only on the shape that actually loses records: a commit happened,
/// and the audit for it came after.</para>
/// <para><b>The debt register.</b> <c>docs/quality/audit-atomicity-debt.txt</c>, same ratchet as the outbox
/// rule: a file may shrink or disappear, never grow, and a file not on the list may have none at all.</para>
/// </remarks>
public class AuditAtomicityTests
{
    private const string DebtFile = "docs/quality/audit-atomicity-debt.txt";

    [Fact]
    public void An_audit_event_is_emitted_before_its_transaction_commits()
    {
        var found = Scan();
        var allowed = ReadDebt();

        var newOffenders = found.Keys.Where(f => !allowed.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        newOffenders.Should().BeEmpty(
            "these files emit an audit event AFTER committing the transaction that made the change, so a "
            + "crash in between leaves a mutation nothing can prove happened. Move the EmitAsync above the "
            + "CommitAsync — the event goes to the same outbox either way:{0}  {1}",
            Environment.NewLine, string.Join($"{Environment.NewLine}  ", newOffenders));

        var grown = found
            .Where(kv => allowed.TryGetValue(kv.Key, out var cap) && kv.Value.Count > cap)
            .Select(kv => $"{kv.Key}: {kv.Value.Count} sites, allowed {allowed[kv.Key]} (lines {string.Join(", ", kv.Value)})")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        grown.Should().BeEmpty(
            "the debt register is one-way — adding a post-commit emit to a file that already has some is "
            + "still adding one:{0}  {1}", Environment.NewLine, string.Join($"{Environment.NewLine}  ", grown));
    }

    /// <summary>A stale entry makes the debt look larger than it is and quietly re-permits a regression in a
    /// file somebody already cleaned — how a ratchet slips back down without anyone editing it.</summary>
    [Fact]
    public void The_debt_register_has_no_stale_entries()
    {
        var found = Scan();
        var stale = ReadDebt().Keys.Where(f => !found.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer emit after commit — remove them from {0} so the count is the real one:{1}  {2}",
            DebtFile, Environment.NewLine, string.Join($"{Environment.NewLine}  ", stale));
    }

    /// <summary>
    /// The detector must distinguish the three shapes, and the middle one is why a naive "is there a
    /// CommitAsync earlier in the file?" scan reports nonsense: minimal-API files hold a dozen handlers, and
    /// one handler's commit sits a few lines above the NEXT handler's emit.
    /// </summary>
    [Fact]
    public void The_detector_reads_handlers_and_not_proximity()
    {
        // (1) The defect: commit, then emit, in one handler.
        Offends("""
            app.MapPost("/a", async () => {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                await audit.EmitAsync(draft, ct);
            });
            """).Should().BeTrue();

        // (2) The fix: emit inside the transaction.
        Offends("""
            app.MapPost("/a", async () => {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await audit.EmitAsync(draft, ct);
                await tx.CommitAsync(ct);
            });
            """).Should().BeFalse();

        // (3) The false positive a proximity scan produces: two SEPARATE handlers, the first committing and
        // the second emitting a few lines later. Nothing is wrong here and flagging it would train people to
        // ignore the rule.
        Offends("""
            app.MapPost("/a", async () => {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await tx.CommitAsync(ct);
            });
            app.MapGet("/b", async () => {
                await audit.EmitAsync(readDraft, ct);
            });
            """).Should().BeFalse();

        // (4) A read audit with no transaction anywhere. There is nothing to be atomic with.
        Offends("""
            app.MapGet("/c", async () => {
                var row = await db.Things.FirstAsync(ct);
                await audit.EmitAsync(readDraft, ct);
            });
            """).Should().BeFalse();

        // (5) EVERY ARM COMMITS AND RETURNS. The approve arm's commit sits above the deny arm's emit, and the
        // two cannot both execute — this is orders' ReportAccess handler, and reading it as an offender is
        // what a "first commit in the handler" rule does.
        Offends("""
            app.MapPost("/e", async () => {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                switch (dec.Decision) {
                    case "approve":
                        await audit.EmitAsync(approved, ct);
                        await tx.CommitAsync(ct);
                        return Results.Ok(a);
                    case "deny":
                        await audit.EmitAsync(denied, ct);
                        await tx.CommitAsync(ct);
                        return Results.Ok(b);
                }
            });
            """).Should().BeFalse();

        // (6) The same shape with an early-returning branch, which is provider onboarding's two-leg terminate.
        Offends("""
            app.MapPost("/f", async () => {
                if (first) {
                    await using var t1 = await db.Database.BeginTransactionAsync(ct);
                    await audit.EmitAsync(opened, ct);
                    await t1.CommitAsync(ct);
                    return Results.Accepted();
                }
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await audit.EmitAsync(approvedDraft, ct);
                await tx.CommitAsync(ct);
                return Results.Ok();
            });
            """).Should().BeFalse();

        // (7) Prose cannot steer it: a comment describing the defect is not the defect.
        Offends("""
            app.MapPost("/d", async () => {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await audit.EmitAsync(draft, ct);
                // Emitted BEFORE tx.CommitAsync(ct) deliberately — see INV-AUDIT-SURVIVES-CRASH.
                await tx.CommitAsync(ct);
            });
            """).Should().BeFalse();
    }

    // ---- the detector ------------------------------------------------------------------------------------

    /// <summary>Repo-relative file → lines of emits that follow a commit inside the same handler.</summary>
    private static SortedDictionary<string, List<int>> Scan()
    {
        var root = SourceScan.RepoRoot();
        var results = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "services"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)
                || rel.Contains("/Tests/", StringComparison.Ordinal)) continue;

            var src = SourceScan.CodeOnly(File.ReadAllText(path));
            foreach (var line in Offenders(src))
            {
                if (!results.TryGetValue(rel, out var lines)) results[rel] = lines = [];
                lines.Add(line);
            }
        }
        return results;
    }

    private static bool Offends(string source) => Offenders(SourceScan.CodeOnly(source)).Count > 0;

    /// <summary>
    /// The line of every <c>EmitAsync</c> that has a <c>CommitAsync</c> before it in the SAME handler.
    /// </summary>
    /// <remarks>
    /// "Same handler" is what makes this readable rather than noisy, and it is the identical boundary the
    /// outbox rule walks: out through enclosing blocks, stopping at the first lambda or method body and never
    /// crossing a type declaration. Two sibling handlers in one minimal-API file share no block, so one's
    /// commit cannot implicate the other's emit.
    /// </remarks>
    private static List<int> Offenders(string src)
    {
        var lines = new List<int>();
        if (!src.Contains("EmitAsync", StringComparison.Ordinal)) return lines;

        foreach (Match emit in Regex.Matches(src, @"EmitAsync\("))
        {
            // The handler — the unit a transaction belongs to. Asked for as a SPAN, not as text: an enclosing
            // block extends past the emit, so recovering its offset by searching for its text before the emit
            // finds nothing and skips the site.
            var spans = SourceScan.EnclosingBlockSpans(src, emit.Index).ToList();
            if (spans.Count == 0) continue;
            var (start, _) = spans[^1];

            /*
             * THE LAST COMMIT ON *THIS PATH*, not the last one in the handler.
             *
             * Taking the handler's first commit reported four correct handlers as offenders, and each was
             * the same shape: a `switch` or an `if` where EVERY arm commits and then returns, so an earlier
             * arm's commit sits above a later arm's emit with a `return` between them. The emit is inside its
             * own arm's transaction and perfectly atomic; the two statements simply cannot both execute.
             *
             * A `return`, `break`, `continue` or `throw` ends that path. So the question is whether a commit
             * is the most recent of the two — if a terminator came after it, the commit belongs to a branch
             * this emit is not on.
             */
            var before = src[start..emit.Index];
            var commit = before.LastIndexOf("CommitAsync(", StringComparison.Ordinal);
            if (commit < 0) continue;

            var terminator = Terminators().Max(t => before.LastIndexOf(t, StringComparison.Ordinal));
            if (terminator > commit) continue;

            lines.Add(src.Take(emit.Index).Count(c => c == '\n') + 1);
        }
        return lines;
    }

    /// <summary>What ends a code path, so a commit before one of these belongs to a different branch.
    /// Matched WITH the semicolon: bare <c>return</c> also appears inside <c>returns</c> in prose, and prose
    /// is already blanked, but the semicolon keeps the intent readable.</summary>
    private static string[] Terminators() => ["return ", "return;", "break;", "continue;", "throw "];

    /// <summary>file → permitted number of post-commit emits.</summary>
    private static Dictionary<string, int> ReadDebt()
    {
        var path = Path.Combine(SourceScan.RepoRoot(), DebtFile);
        File.Exists(path).Should().BeTrue("{0} is the debt register this rule ratchets down", DebtFile);

        var debt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            parts.Should().HaveCount(2, "each entry is '<repo-relative-path> <count>'; got: {0}", line);
            debt[parts[0]] = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        return debt;
    }
}
