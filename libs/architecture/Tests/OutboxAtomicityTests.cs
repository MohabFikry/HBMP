using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// Phase 24 Gate 3 — INV-OUTBOX-SURVIVES-CRASH, enforced where it is actually breakable.
///
/// <para>CLAUDE.md: "Publish domain events via <b>outbox</b> in the same transaction as the state change."
/// <c>EfOutbox.EnqueueRawAsync</c> calls its own <c>SaveChangesAsync</c>, so a handler that commits its
/// business change and THEN enqueues has two separate commits with a window between them. A process kill in
/// that window leaves the state changed and the event gone forever — and nothing anywhere records that it
/// was owed, so no relay, retry or replay will ever produce it. Enqueueing first is not better: it publishes
/// an event for a state change that may never commit, and a phantom event cannot be un-sent.
/// <c>EfOutboxDurabilityTests</c> demonstrates both halves against a real database.</para>
///
/// <para>The library cannot enforce this on its own — whether the two writes share a transaction is a
/// property of the CALL SITE. So this reads the source: for every <c>EnqueueAsync</c>, it walks outward to
/// the nearest enclosing block that also performs a <c>SaveChangesAsync</c>, and requires a
/// <c>BeginTransactionAsync</c> in that block. An omission cannot fail a unit test, because the test that
/// would catch it is the test nobody wrote either — the same reasoning that created this project.</para>
///
/// <para><b>The debt register.</b> 85 call sites across 30 files predate this rule. They are listed in
/// <c>docs/quality/outbox-atomicity-debt.txt</c> with their counts, and the rule is a RATCHET: a file may
/// shrink or disappear, never grow, and a file not on the list may have none at all. Recording them is not
/// forgiving them — it is the difference between debt somebody can see and a rule nobody can land. Every
/// number in that file is a place where a state change can outlive its event.</para>
/// </summary>
public class OutboxAtomicityTests
{
    private const string DebtFile = "docs/quality/outbox-atomicity-debt.txt";

    [Fact]
    public void A_state_change_and_its_event_share_one_transaction()
    {
        var found = Scan();
        var allowed = ReadDebt();

        var newOffenders = found.Keys.Where(f => !allowed.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        newOffenders.Should().BeEmpty(
            "these files enqueue a domain event in the same block as a SaveChangesAsync without a " +
            "transaction around both, so a crash between the two commits loses the event (or publishes one " +
            "for a state change that never happened). Wrap them in a single BeginTransactionAsync:{0}  {1}",
            Environment.NewLine, string.Join($"{Environment.NewLine}  ", newOffenders));

        // The ratchet: a listed file may shrink or vanish, never grow.
        var grown = found
            .Where(kv => allowed.TryGetValue(kv.Key, out var cap) && kv.Value.Count > cap)
            .Select(kv => $"{kv.Key}: {kv.Value.Count} sites, allowed {allowed[kv.Key]} (lines {string.Join(", ", kv.Value)})")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        grown.Should().BeEmpty(
            "the debt register is one-way — adding a non-transactional enqueue to a file that already has " +
            "some is still adding one:{0}  {1}", Environment.NewLine, string.Join($"{Environment.NewLine}  ", grown));
    }

    /// <summary>
    /// The register must describe reality. A stale entry — a file that has been fixed, or deleted — makes
    /// the debt look larger than it is and quietly re-permits a regression in a file somebody already
    /// cleaned, which is how a ratchet slips back down without anyone editing it.
    /// </summary>
    [Fact]
    public void The_debt_register_has_no_stale_entries()
    {
        var found = Scan();
        var stale = ReadDebt().Keys
            .Where(f => !found.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer have a non-transactional enqueue — remove them from {0} so the count " +
            "is the real one and the ratchet cannot slip back:{1}  {2}",
            DebtFile, Environment.NewLine, string.Join($"{Environment.NewLine}  ", stale));
    }

    // ---- the detector ------------------------------------------------------------------------------------

    /// <summary>Repo-relative file → line numbers of enqueue sites that share a block with a SaveChanges
    /// and have no transaction around them.</summary>
    private static SortedDictionary<string, List<int>> Scan()
    {
        var root = RepoRoot();
        var results = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "services"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)
                || rel.Contains("/Tests/", StringComparison.Ordinal)) continue;

            var src = File.ReadAllText(path);
            if (!src.Contains("EnqueueAsync", StringComparison.Ordinal)) continue;

            foreach (Match m in Regex.Matches(src, @"EnqueueAsync\("))
            {
                // Every block from the enqueue out to its handler. The transaction may legitimately be
                // declared further out than the block that saves — `await using var tx = ...` before a
                // `switch` covers every case arm inside it — so the question is whether ANY block in the
                // handler opens one.
                //
                // The rule does NOT require a visible write. It used to, and that was a hole: in emr,
                // pharmacy dispensing, orders consume and every claims endpoint, the state change happens
                // inside a service that owns and commits its OWN transaction, and the handler enqueues after
                // it returns — the exact two-commit shape this rule exists to forbid, with no
                // SaveChangesAsync in the handler for a detector to find. Requiring a visible write made the
                // rule blind to 40 sites, concentrated in the money paths. So the question is simply: is
                // this enqueue inside a transaction? An enqueue that genuinely announces nothing (a pure
                // notification with no state behind it) is rare enough to earn a register entry.
                var scope = EnclosingBlocks(src, m.Index).ToList();
                if (scope.Any(b => b.Contains("BeginTransactionAsync", StringComparison.Ordinal))) continue;

                var line = src.Take(m.Index).Count(c => c == '\n') + 1;
                if (!results.TryGetValue(rel, out var lines)) results[rel] = lines = [];
                lines.Add(line);
            }
        }
        return results;
    }

    /// <summary>A block writes state if it saves directly OR through the house helpers that save. Keying
    /// only on <c>SaveChangesAsync</c> would miss <c>SaveOrConflict</c> (the membership write path's own
    /// wrapper) and the ExecuteUpdate/Delete forms, which bypass the change tracker and commit on their
    /// own — the very reason they need to be inside the transaction too.</summary>
    private static bool Mutates(string block) =>
        block.Contains("SaveChangesAsync", StringComparison.Ordinal)
        || block.Contains("SaveOrConflict", StringComparison.Ordinal)
        || block.Contains("ExecuteUpdateAsync", StringComparison.Ordinal)
        || block.Contains("ExecuteDeleteAsync", StringComparison.Ordinal);

    /// <summary>
    /// The brace-delimited blocks enclosing <paramref name="index"/>, innermost first, stopping at the
    /// HANDLER that owns it.
    ///
    /// <para>Where the walk stops decides what the rule can see, in both directions. Stop too early — at the
    /// innermost block that saves — and a transaction declared before a <c>switch</c> is invisible, so a
    /// correctly-wrapped handler reads as an offender. Walk too far — into the method holding a dozen
    /// minimal-API lambdas, or into the class — and one lambda's <c>BeginTransactionAsync</c> clears every
    /// other lambda beside it, which is a false negative exactly where a file is big enough for two writes to
    /// drift apart.</para>
    ///
    /// <para>So the walk ends after the first LAMBDA body (a block whose head ends in <c>=&gt;</c>) or method
    /// body, and never crosses a type or namespace declaration. That is the handler: the unit a transaction
    /// belongs to.</para>
    /// </summary>
    private static IEnumerable<string> EnclosingBlocks(string src, int index)
    {
        var pos = index;
        for (var level = 0; level < 8; level++)
        {
            var start = OpenBraceBefore(src, pos);
            if (start < 0) yield break;
            var head = src[Math.Max(0, start - 400)..start];
            if (Regex.IsMatch(head, @"\b(class|record|struct|interface|enum|namespace)\s+\w+[^;{]*$", RegexOptions.Singleline))
                yield break;

            var end = MatchingClose(src, start);
            if (end < 0) yield break;
            yield return src[start..(end + 1)];

            // A LAMBDA body ends the handler: minimal-API files put a dozen of them in one method, and
            // walking past it would let one lambda's transaction clear every sibling beside it.
            //
            // Nothing else stops the walk. A `\)$` test for "method signature" looks right and is wrong —
            // `switch (...)`, `if (...)`, `foreach (...)` all end in `)`, so it stopped at the first control
            // block and reported four correctly-wrapped ReportAccess handlers as offenders because their
            // `await using var tx` sits before the `switch` rather than inside it. For a real method body
            // the type-declaration check above is the boundary, which is the right one.
            if (Regex.IsMatch(head, @"=>\s*$")) yield break;
            pos = start;
        }
    }

    private static int OpenBraceBefore(string src, int from)
    {
        var depth = 0;
        for (var i = from - 1; i >= 0; i--)
        {
            if (src[i] == '}') depth++;
            else if (src[i] == '{')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }

    private static int MatchingClose(string src, int open)
    {
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>file → permitted number of non-transactional enqueue sites.</summary>
    private static Dictionary<string, int> ReadDebt()
    {
        var path = Path.Combine(RepoRoot(), DebtFile);
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
