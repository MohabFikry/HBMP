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
/// property of the CALL SITE. So this reads the source: for every <c>EnqueueAsync</c>, it asks whether any
/// block between that call and its handler opens a transaction. An omission cannot fail a unit test,
/// because the test that would catch it is the test nobody wrote either — the same reasoning that created
/// this project.</para>
///
/// <para>It does NOT require a visible write, and that cost a correction. An earlier version only flagged an
/// enqueue sharing a handler with a <c>SaveChangesAsync</c> — and in emr, orders consume and every claims
/// endpoint the write happens inside a service that owns and commits its own transaction, leaving nothing in
/// the handler to find. The rule was blind to 40 sites, concentrated in the money paths, while the register
/// read as though they did not exist.</para>
///
/// <para>Two shapes are legitimately exempt and both are recognised: a transaction anywhere in the handler
/// scope, and pharmacy's <c>insideTransaction:</c> callback, which the service invokes after its write and
/// before its commit — the correct fix for the service-owned case, since wrapping the handler would nest a
/// second transaction and throw.</para>
///
/// <para><b>The debt register.</b> The sites that predate this rule are listed in
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

    /// <summary>
    /// The detector must read CODE. Each case below is a way prose or a string literal could have steered it,
    /// and the middle one is not hypothetical: BulkJobEngine's doc comment contains "no record of where it
    /// stopped", which read as a type declaration, ended the block walk early and hid the transaction the
    /// method actually opens. The third is the dangerous direction — a comment that merely MENTIONS
    /// BeginTransactionAsync clearing a genuine offender.
    /// </summary>
    [Fact]
    public void The_detector_reads_code_not_prose()
    {
        const string src = """
            var url = "https://example/a"; var brace = "}"; var json = $"{{\"x\":1}}";
            // this comment mentions EnqueueAsync( and would be a site if comments were code
            /* and this one says record BulkCommitReport, which is not a type declaration */
            // BeginTransactionAsync appears here in prose only
            """;

        var code = CodeOnly(src);

        code.Should().HaveLength(src.Length, "offsets and line numbers must survive blanking");
        code.Count(c => c == '\n').Should().Be(src.Count(c => c == '\n'));
        code.Should().NotContain("EnqueueAsync", "a comment is not a call site");
        code.Should().NotContain("record BulkCommitReport", "a comment is not a declaration");
        code.Should().NotContain("BeginTransactionAsync",
            "otherwise a comment mentioning it would clear a real offender — the failure direction that " +
            "makes a ratchet look tightened while it is not");
        code.Should().NotContain("https", "a // inside a string literal must not eat the rest of the line");
        code.Should().Contain("var url =", "code outside the literals is untouched");
        code.Should().Contain("var json =");
        code.Count(c => c == '{').Should().Be(0, "braces inside string literals must not reach the brace walk");
        code.Count(c => c == '}').Should().Be(0);
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

            // Comments and string literals are blanked FIRST. Reading them as code broke this rule in both
            // directions: BulkJobEngine's doc comment says "...with no record of where it stopped", and
            // `record of where it stopped` satisfied the "did we just walk into a type declaration?" test, so
            // the walk stopped one level early, the method's own transaction became invisible, and a
            // correctly-wrapped enqueue was reported as debt. The same reading would have counted an
            // `EnqueueAsync(` written inside a comment as a real site, and — the dangerous direction — let a
            // `BeginTransactionAsync` mentioned only in a comment CLEAR a genuine offender. A rule this one
            // is load-bearing cannot be steerable by prose.
            var src = CodeOnly(File.ReadAllText(path));
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
                if (InsideTransactionCallback(src, m.Index)) continue;

                var line = src.Take(m.Index).Count(c => c == '\n') + 1;
                if (!results.TryGetValue(rel, out var lines)) results[rel] = lines = [];
                lines.Add(line);
            }
        }
        return results;
    }

    /// <summary>
    /// The house pattern for the service-owned case, and the reason it is not a violation.
    ///
    /// <para>Where the write lives inside a service that owns its transaction, the handler cannot wrap it —
    /// a second transaction would nest inside the service's own and throw. pharmacy's DispenseExecutor
    /// solves this by taking an <c>insideTransaction:</c> callback and invoking it after the write and
    /// BEFORE its commit, so the enqueue joins that same transaction while the payload is still built at the
    /// handler, where the vocabulary belongs. An enqueue inside such a callback is atomic by construction,
    /// and flagging it would push people away from the one pattern that actually fixes this shape.</para>
    /// </summary>
    private static bool InsideTransactionCallback(string src, int index)
    {
        var marker = src.LastIndexOf("insideTransaction:", index, StringComparison.Ordinal);
        if (marker < 0) return false;
        var open = src.IndexOf('{', marker);
        if (open < 0 || open > index) return false;
        return MatchingClose(src, open) > index;
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

    /// <summary>
    /// The same source with every comment, string literal and char literal blanked to spaces — newlines
    /// preserved, so every offset and line number is unchanged and the detector can still report where.
    ///
    /// <para>Blanking is not cosmetic. The brace walk and the keyword tests below both read raw text, so a
    /// brace inside a JSON string (<c>AfterState = $"{{\"enabled\":true}}"</c>), a <c>//</c> inside a URL, or
    /// an English sentence containing the word <c>record</c> all steer a rule that is supposed to be reading
    /// code. Delimiters go too: nothing downstream needs them, and leaving them invites the next reader to
    /// assume the string is still there.</para>
    /// </summary>
    private static string CodeOnly(string src)
    {
        var buf = src.ToCharArray();
        void Erase(int from, int to)
        {
            for (var k = from; k < to && k < buf.Length; k++)
                if (buf[k] is not ('\n' or '\r')) buf[k] = ' ';
        }

        for (var i = 0; i < src.Length; i++)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                var end = src.IndexOf('\n', i);
                if (end < 0) end = src.Length;
                Erase(i, end);
                i = end;
                continue;
            }
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var close = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? src.Length : close + 2;
                Erase(i, end);
                i = end - 1;
                continue;
            }
            if (src[i] == '\'')
            {
                var k = i + 1;
                while (k < src.Length && src[k] != '\'') k += src[k] == '\\' ? 2 : 1;
                Erase(i, Math.Min(k + 1, src.Length));
                i = k;
                continue;
            }

            // A string literal may carry any run of $ and @ prefixes: "", @"", $"", $@"", @$"", """ raw """.
            var p = i;
            while (p < src.Length && (src[p] == '$' || src[p] == '@')) p++;
            if (p >= src.Length || src[p] != '"') continue;

            var verbatim = src.AsSpan(i, p - i).Contains('@');
            var q = p;
            while (q < src.Length && src[q] == '"') q++;
            var quotes = q - p;

            int stop;
            if (quotes >= 3)
            {
                // Raw string: the terminator is a quote run of the same length.
                var close = src.IndexOf(new string('"', quotes), q, StringComparison.Ordinal);
                stop = close < 0 ? src.Length : close + quotes;
            }
            else if (quotes == 2)
            {
                stop = q;   // the empty string
            }
            else if (verbatim)
            {
                var k = p + 1;
                while (k < src.Length)
                {
                    if (src[k] != '"') { k++; continue; }
                    if (k + 1 < src.Length && src[k + 1] == '"') { k += 2; continue; }   // "" is one quote
                    break;
                }
                stop = Math.Min(k + 1, src.Length);
            }
            else
            {
                var k = p + 1;
                while (k < src.Length && src[k] != '"' && src[k] != '\n') k += src[k] == '\\' ? 2 : 1;
                stop = Math.Min(k + 1, src.Length);
            }

            Erase(i, stop);
            i = stop - 1;
        }
        return new string(buf);
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
