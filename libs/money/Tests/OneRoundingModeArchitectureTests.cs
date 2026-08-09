using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Money.Tests;

/// <summary>
/// 2026-08-09 audit — an amount is rounded ONE way on this platform, and it is banker's.
///
/// <para><b>The failure.</b> <see cref="Money"/> rounds half-to-even at construction, and says why: half away
/// from zero biases every .005 upward, which across a settlement batch of thousands of lines is a systematic
/// overpayment. But <c>Mersal.Money</c> is adopted by claims and eligibility only. Policy's tier limits and
/// reporting's per-member-per-month figures were bare decimals rounded <c>AwayFromZero</c> — so the same
/// amount came out a piastre higher on a dashboard than in the settlement it was read beside, and neither
/// screen could tell you which one was wrong.</para>
///
/// <para><b>The rule.</b> Rounding to <see cref="Money.Scale"/> decimal places is rounding an AMOUNT, and
/// must be <c>ToEven</c>. Coarser scales are not amounts and are left alone — every 1dp round in this
/// codebase is a displayed percentage, where the mode is cosmetic and consistency with a payment is not the
/// question being asked.</para>
///
/// <para><b>What the rule cannot know.</b> Two decimal places is a strong signal, not a proof: reporting
/// rounds a MONTH COUNT to 2dp. That site says so inline with <c>// money-scale: not-money (reason)</c>,
/// per-site rather than per-file — the same acknowledgement shape the Cairo-date rule uses next door, and for
/// the same reason. A file-level exemption covers the next amount somebody adds to that file.</para>
///
/// <para>Note this rule deliberately does NOT require adopting the <c>Money</c> TYPE in policy, pharmacy,
/// finance and reporting. That is a much larger migration — hundreds of signatures and the EF mapping layer —
/// and it is a plan item, not a defect. What it forbids is the four services disagreeing about arithmetic
/// while they remain decimals.</para>
/// </summary>
public class OneRoundingModeArchitectureTests
{
    /// <summary>A round to exactly <see cref="Money.Scale"/> places using half-away-from-zero.</summary>
    private static readonly Regex AwayAtMoneyScale =
        new(@"Round\s*\([^;]*?,\s*2\s*,\s*MidpointRounding\s*\.\s*AwayFromZero", RegexOptions.Compiled);

    private static readonly Regex Acknowledged =
        new(@"//\s*money-scale:\s*not-money\s*\(.+\)", RegexOptions.Compiled);

    [Fact]
    public void An_amount_is_rounded_half_to_even()
    {
        Money.Scale.Should().Be(2, "the regex below is written for the platform's settlement scale");

        var offenders = Offenders().ToList();

        offenders.Should().BeEmpty(
            "rounding to 2dp is rounding an amount, and every amount on this platform rounds ToEven — see "
            + "Mersal.Money for why half-away-from-zero is a systematic overpayment across a batch. If the "
            + "value is genuinely not money, say so with `// money-scale: not-money (reason)`:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_detector_reads_the_scale_and_the_mode()
    {
        Flags("var x = Math.Round(a / b, 2, MidpointRounding.AwayFromZero);").Should().BeTrue();
        Flags("return decimal.Round(limit * m, 2, MidpointRounding.AwayFromZero);").Should().BeTrue();

        // The fix, and the two things that are not the defect.
        Flags("var x = Math.Round(a / b, 2, MidpointRounding.ToEven);").Should().BeFalse();
        Flags("var pct = Math.Round(c / l * 100m, 1, MidpointRounding.AwayFromZero);").Should().BeFalse();

        // Acknowledged, on the line and on the line above.
        Flags("var m = Math.Round(d / 30.44m, 2, MidpointRounding.AwayFromZero); // money-scale: not-money (months)")
            .Should().BeFalse();
        Flags("// money-scale: not-money (months)\nvar m = Math.Round(d / 30.44m, 2, MidpointRounding.AwayFromZero);")
            .Should().BeFalse();

        // A marker with no reason is not an acknowledgement.
        Flags("var x = Math.Round(a, 2, MidpointRounding.AwayFromZero); // money-scale: not-money").Should().BeTrue();
    }

    [Fact]
    public void Every_acknowledgement_still_sits_on_a_line_that_needs_one()
    {
        var stale = new List<string>();
        foreach (var (relative, absolute) in Sources())
        {
            var lines = File.ReadAllLines(absolute);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Acknowledged.IsMatch(lines[i])) continue;
                var covers = AwayAtMoneyScale.IsMatch(lines[i])
                             || (i + 1 < lines.Length && AwayAtMoneyScale.IsMatch(lines[i + 1]));
                if (!covers) stale.Add($"{relative}:{i + 1}");
            }
        }

        stale.Should().BeEmpty(
            "these `money-scale: not-money` acknowledgements no longer sit on a 2dp AwayFromZero round — "
            + "remove them:\n  " + string.Join("\n  ", stale));
    }

    // ── the detector ──────────────────────────────────────────────────────────────────────────────

    private static bool Flags(string source) => Hits(source.Split('\n')).Any();

    private static IEnumerable<int> Hits(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!AwayAtMoneyScale.IsMatch(lines[i])) continue;
            if (Acknowledged.IsMatch(lines[i])) continue;
            if (i > 0 && Acknowledged.IsMatch(lines[i - 1])) continue;
            yield return i + 1;
        }
    }

    private static IEnumerable<string> Offenders()
    {
        foreach (var (relative, absolute) in Sources())
            foreach (var line in Hits(File.ReadAllLines(absolute)))
                yield return $"{relative}:{line}";
    }

    /// <summary>Production C#, minus <c>Money.cs</c> itself — its doc comment quotes the banned mode while
    /// explaining why it is banned, which is the one place saying the words is the point.</summary>
    private static IEnumerable<(string Relative, string Absolute)> Sources()
    {
        var root = RepoRoot();
        foreach (var area in new[] { "services", "libs" })
        {
            var dir = Path.Combine(root, area);
            if (!Directory.Exists(dir)) continue;
            foreach (var abs in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.Contains("/obj/", StringComparison.Ordinal)
                    || rel.Contains("/Tests/", StringComparison.Ordinal)
                    || rel == "libs/money/Money.cs")
                    continue;
                yield return (rel, abs);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (HbmpPlatform.sln) not found");
    }
}
