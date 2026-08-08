using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.MasterData.Tests;

/// <summary>
/// 30.6 — the guard for design 45 §2's decision, tested by design 46 §8's request.
///
/// <para>§2 made <c>is_session_based</c> DATA rather than code so that adding a session-based therapy would
/// be an INSERT: "it must never be <c>if (type === 'Physiotherapy')</c>: dialysis and rehabilitation are
/// session-based too, so hard-coding the name guarantees the same conversation twice more — and the second
/// and third times it will be written as a second and third special case rather than as this flag."</para>
///
/// <para>§8 then asks for Occupational Therapy and Speech Therapy, "both is_session_based = true — the same
/// shape as physiotherapy, which is exactly why that flag was made data rather than code." The phase-30
/// prompt is blunt about the stakes: "This is a DATA change only; if it needs a code change, the ../45 §2
/// flag was not implemented as designed and that is the bug to fix."</para>
///
/// <para>So this test asserts an ABSENCE, which is the only way to check that claim. A conditional on a type
/// NAME would compile, pass every functional test, and silently make the sixth therapy a code change
/// again.</para>
/// </summary>
public class ProcedureTypeIsDataNotCodeTests
{
    /// <summary>Every session-based type currently seeded. The two added in 30.6 are here because a guard
    /// that only knew the old ones would not notice a new special case written for the new ones.</summary>
    private static readonly string[] SessionBasedTypes =
        ["Physiotherapy", "Dialysis", "Rehabilitation", "OccupationalTherapy", "SpeechTherapy"];

    [Fact]
    public void The_two_new_therapies_are_seeded_as_session_based()
    {
        var sql = File.ReadAllText(Path.Combine(
            RepoRoot(), "services/masterdata/Infrastructure/Migrations/0017_therapy_procedure_types.sql"));

        foreach (var code in new[] { "OccupationalTherapy", "SpeechTherapy" })
            Regex.IsMatch(sql, $@"'{code}',[^\n]*?,\s*true,").Should().BeTrue(
                "'{0}' must be seeded with is_session_based = true — it is the same shape as physiotherapy",
                code);

        sql.Should().Contain("العلاج الوظيفي").And.Contain("علاج النطق",
            "a procedure type is shown to a prescriber in both languages, and an Arabic name that is missing "
            + "is one somebody will machine-translate later");
    }

    [Fact]
    public void No_source_file_branches_on_a_PROCEDURE_TYPE_NAME()
    {
        // THE POINT OF THE FLAG. A comparison against a type name is how "adding Hydrotherapy is an INSERT"
        // quietly becomes "adding Hydrotherapy is a release".
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var type in SessionBasedTypes)
            {
                // A NAME inside a comparison, not a name in prose or a seed. Matches `== "Physiotherapy"`,
                // `=== 'Physiotherapy'`, `case "Physiotherapy"` and `Equals("Physiotherapy")`.
                foreach (Match m in Regex.Matches(
                             text, $@"(==|===|!=|!==|\bcase\b|Equals\(|\.Contains\()\s*[""']{type}[""']"))
                {
                    var line = text[..m.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Relative(file)}:{line} branches on the type name '{type}'");
                }
            }
        }

        offenders.Should().BeEmpty(
            "session-based behaviour follows the is_session_based FLAG, never the type's name — otherwise "
            + "the next therapy is a code change and the flag was decoration:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepoRoot();
        foreach (var dir in new[] { "services", "libs", "apps/web/src" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;
            foreach (var f in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (ext is not (".cs" or ".ts" or ".tsx")) continue;
                var rel = Relative(f);
                // Tests may name a type freely — a fixture that prescribes physiotherapy is not a special
                // case in the product. node_modules and build output are not source.
                if (rel.Contains("/Tests/", StringComparison.Ordinal)
                    || rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.Contains("/obj/", StringComparison.Ordinal)
                    || rel.Contains("node_modules", StringComparison.Ordinal)
                    || rel.Contains("/test/", StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
    }

    private static string Relative(string p) => Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
