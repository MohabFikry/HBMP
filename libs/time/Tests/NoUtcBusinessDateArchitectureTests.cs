using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Time.Tests;

/// <summary>
/// 2026-08-09 audit — a BUSINESS DATE may not be read off a UTC instant.
///
/// <para><b>The gap this closes.</b> <see cref="NoBareClockArchitectureTests"/> already forbids reading the
/// wall clock directly, and it passes with almost no exceptions — every service takes an injected
/// <c>TimeProvider</c>. That turned out to prove less than it looked like. Taking the injected clock and then
/// writing <c>DateOnly.FromDateTime(now.UtcDateTime)</c> is perfectly testable and exactly as wrong: the
/// value is a UTC calendar date, and every date decision on this platform is an Egyptian one. The rule
/// against bare clocks made the instant injectable and said nothing about the conversion.</para>
///
/// <para><b>What it costs.</b> Cairo is UTC+2 (UTC+3 in summer), so for the first two to three hours of every
/// Cairo day the UTC date is still yesterday. The audit found it at the pharmacy counter: a chronic refill
/// window that opens today is evaluated against yesterday's date, and a patient who arrives between 00:00 and
/// 02:00 Cairo on the day their medicine is due is refused it. Same shape elsewhere — a lot that expires
/// today still dispensable, an enrolment stamped a day early, a birthdate check that accepts tomorrow.</para>
///
/// <para><b>The offset probe is not an offender.</b> Converting an instant to Cairo requires knowing Cairo's
/// offset ON that instant's date, and the only date available before the conversion is the UTC one. So
/// <c>offsetFor(DateOnly.FromDateTime(instant.UtcDateTime))</c> is a correct and necessary step — the UTC date
/// is a lookup key there, never an answer. Those sites acknowledge themselves inline with
/// <c>// cairo-date: offset-probe (reason)</c>, per-site rather than per-file: an exemption granted to a whole
/// file is one that silently covers the next date bug written into it.</para>
/// </summary>
public class NoUtcBusinessDateArchitectureTests
{
    /// <summary>
    /// <c>DateOnly.FromDateTime(…)</c> whose argument reads a UTC instant. `\w` and dots only inside, so this
    /// matches the direct conversion and stops at anything more structured — a nested call like
    /// <c>ToOffset(probe).DateTime</c> is the CONVERTED value and is what correct code looks like.
    /// </summary>
    private static readonly Regex UtcDate =
        new(@"DateOnly\s*\.\s*FromDateTime\s*\(\s*[\w.()]*?\.\s*(UtcDateTime|UtcNow)\b", RegexOptions.Compiled);

    /// <summary>The inline acknowledgement, which must carry a parenthesised reason. A bare marker would be a
    /// way to silence the rule without saying anything.</summary>
    private static readonly Regex Acknowledged =
        new(@"//\s*cairo-date:\s*offset-probe\s*\(.+\)", RegexOptions.Compiled);

    [Fact]
    public void No_business_date_is_read_off_a_UTC_instant()
    {
        var offenders = Offenders().ToList();

        offenders.Should().BeEmpty(
            "a business date must come from IBusinessCalendar (Today() for now, DateOf(instant) for a "
            + "recorded event). Reading DateOnly off a UTC instant is wrong for the first two to three hours "
            + "of every Cairo day. If the UTC date is genuinely a lookup key for a zone offset, say so with "
            + "`// cairo-date: offset-probe (reason)` on that line or the one above it:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The rule must SEE the shape it forbids, or it is a test that passes because it looks at nothing. This
    /// pins both the offender and each thing that must not be mistaken for one.
    /// </summary>
    [Fact]
    public void The_detector_tells_a_UTC_date_from_a_converted_one()
    {
        // The defect.
        Flags("var today = DateOnly.FromDateTime(now.UtcDateTime);").Should().BeTrue();
        Flags("var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);").Should().BeTrue();
        Flags("if (expiry <= DateOnly.FromDateTime(DateTime.UtcNow)) return null;").Should().BeTrue();

        // The fix.
        Flags("var today = calendar.Today();").Should().BeFalse();
        Flags("var day = calendar.DateOf(recordedAt);").Should().BeFalse();

        // An already-converted instant. `.DateTime` off a ToOffset result is the Cairo local time, which is
        // the whole point — flagging it would forbid the correct implementation.
        Flags("return DateOnly.FromDateTime(instant.ToOffset(cairo).DateTime);").Should().BeFalse();

        // The acknowledged probe, both placements.
        Flags("var probe = offsetFor(DateOnly.FromDateTime(i.UtcDateTime));  // cairo-date: offset-probe (key)")
            .Should().BeFalse();
        Flags("// cairo-date: offset-probe (need the zone offset first)\nvar p = DateOnly.FromDateTime(i.UtcDateTime);")
            .Should().BeFalse();

        // A marker with no reason is not an acknowledgement.
        Flags("var t = DateOnly.FromDateTime(now.UtcDateTime); // cairo-date: offset-probe").Should().BeTrue();
    }

    [Fact]
    public void Every_acknowledgement_still_sits_on_a_line_that_needs_one()
    {
        // A marker left behind after its conversion was rewritten reads as "this was considered" on code
        // nobody has looked at since. Same argument as the allowlist-staleness test next door.
        var stale = new List<string>();
        foreach (var (relative, absolute) in Sources())
        {
            var lines = File.ReadAllLines(absolute);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Acknowledged.IsMatch(lines[i])) continue;
                var covers = UtcDate.IsMatch(lines[i]) || (i + 1 < lines.Length && UtcDate.IsMatch(lines[i + 1]));
                if (!covers) stale.Add($"{relative}:{i + 1}");
            }
        }

        stale.Should().BeEmpty(
            "these `cairo-date: offset-probe` acknowledgements no longer sit on a UTC-date conversion — "
            + "remove them:\n  " + string.Join("\n  ", stale));
    }

    // ── the detector ──────────────────────────────────────────────────────────────────────────────

    private static bool Flags(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!UtcDate.IsMatch(lines[i])) continue;
            if (Acknowledged.IsMatch(lines[i])) continue;
            if (i > 0 && Acknowledged.IsMatch(lines[i - 1])) continue;
            return true;
        }
        return false;
    }

    private static IEnumerable<string> Offenders()
    {
        foreach (var (relative, absolute) in Sources())
        {
            var lines = File.ReadAllLines(absolute);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!UtcDate.IsMatch(lines[i])) continue;
                if (Acknowledged.IsMatch(lines[i])) continue;
                if (i > 0 && Acknowledged.IsMatch(lines[i - 1])) continue;
                yield return $"{relative}:{i + 1}";
            }
        }
    }

    /// <summary>Production C# under services/ and libs/, minus the calendar itself — it is the one place the
    /// conversion is defined, and it does not take the UTC date as an answer.</summary>
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
                    || rel.Contains("/Migrations/", StringComparison.Ordinal)
                    || rel == "libs/time/BusinessCalendar.cs")
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
