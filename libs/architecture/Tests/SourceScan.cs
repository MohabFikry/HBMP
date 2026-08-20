using System.Text.RegularExpressions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// The source-reading primitives the atomicity rules share.
/// </summary>
/// <remarks>
/// <para>Extracted when a SECOND rule needed them. <see cref="OutboxAtomicityTests"/> asks "is this enqueue
/// inside a transaction?"; <see cref="AuditAtomicityTests"/> asks "does this audit emit happen after the
/// commit?". Different questions, identical reading problem — and a second private copy of a brace walk that
/// has already been corrected twice (once for a doc comment containing the word <c>record</c>, once for a
/// <c>)</c>-ending <c>switch</c> head) is a copy that will be corrected once and stay wrong in the other.</para>
/// <para>Every method here is the ORIGINAL, moved verbatim. The outbox rule's own self-tests and its debt
/// register both still pass against it, which is what makes the move safe to claim.</para>
/// </remarks>
internal static class SourceScan
{
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
    internal static IEnumerable<string> EnclosingBlocks(string src, int index)
    {
        foreach (var (start, end) in EnclosingBlockSpans(src, index)) yield return src[start..(end + 1)];
    }

    /// <summary>
    /// The same walk, reporting WHERE each block is rather than what it contains.
    /// </summary>
    /// <remarks>
    /// A caller that needs to compare positions inside the block against a position in the file cannot
    /// recover the offset from the text: <c>LastIndexOf(block, index)</c> requires the whole match to fit
    /// before <c>index</c>, and an enclosing block by definition extends past it, so the search returns -1
    /// and the site is silently skipped. Asking the walk for the span instead removes the question.
    /// </remarks>
    internal static IEnumerable<(int Start, int End)> EnclosingBlockSpans(string src, int index)
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
            yield return (start, end);

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
    internal static string CodeOnly(string src)
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

    internal static int OpenBraceBefore(string src, int from)
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

    internal static int MatchingClose(string src, int open)
    {
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
