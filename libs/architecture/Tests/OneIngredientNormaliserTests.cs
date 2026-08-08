using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// 28.1 — the platform normalises an ingredient name in exactly ONE place (design 44 §1.2, §7).
///
/// <para>
/// "What molecule is this, and what molecules is this product made of" is asked by three subsystems that
/// must agree: the prescribing engine looks a manufacturer label up by it, masterdata decomposes a product
/// into ingredients so an allergy, interaction or duplicate-therapy rule can be keyed on one, and the loader
/// populates those rows. A second implementation does not fail loudly — it drifts. The day the two disagree,
/// a pharmacist authors a rule against one ingredient and a prescription is screened against another, and
/// nothing anywhere reports a problem.
/// </para>
///
/// <para>
/// The salt-suffix rule is the sharpest illustration of why a copy is dangerous. It is stripped from the END
/// of a name only, because stripping it anywhere turns "sodium chloride" into "chloride" — which matches
/// benzalkonium chloride, a disinfectant. A well-meaning simplification of a duplicate would reintroduce
/// exactly that, in the copy nobody was reading.
/// </para>
///
/// <para>
/// Design 44 §6 makes the same demand of ICD dot-normalisation, for the same reason; phase 28 Gate 7 adds
/// that assertion below this one.
/// </para>
/// </summary>
public class OneIngredientNormaliserTests
{
    /// <summary>The one file entitled to define it.</summary>
    private const string Home = "libs/ingredients/IngredientTokens.cs";

    [Fact]
    public void Only_one_file_defines_the_ingredient_normaliser()
    {
        var definitions = ProductionFiles()
            .Where(f => Regex.IsMatch(
                File.ReadAllText(f.Absolute),
                @"(?:static\s+)?(?:partial\s+)?class\s+IngredientTokens\b"))
            .Select(f => f.Relative)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        definitions.Should().Equal([Home],
            "the ingredient normaliser is shared vocabulary, not a utility worth copying — two "
            + "implementations of a matching rule diverge silently, and the divergence surfaces as a rule "
            + "screening the wrong molecule rather than as a failing test");
    }

    [Fact]
    public void No_second_salt_stripping_or_INN_USAN_table_exists()
    {
        // The two pieces of knowledge most likely to be re-derived by someone who did not know this library
        // exists: the salt/hydrate suffix list, and the INN↔USAN spelling map that makes "paracetamol" find
        // a label published as "acetaminophen".
        var offenders = ProductionFiles()
            .Where(f => f.Relative != Home)
            .Where(f =>
            {
                var text = File.ReadAllText(f.Absolute);
                return Regex.IsMatch(text, @"hydrochloride\s*\|") // a salt-suffix alternation
                    || Regex.IsMatch(text, @"""paracetamol""\s*,\s*""acetaminophen""");
            })
            .Select(f => f.Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "salt-suffix stripping and the INN/USAN map belong to {0} alone. A copy that strips a salt "
            + "anywhere rather than only at the end turns 'sodium chloride' into 'chloride', which matches a "
            + "disinfectant — and reading interaction advice off the wrong molecule's label is worse than "
            + "not checking at all", Home);
    }

    private static IEnumerable<(string Absolute, string Relative)> ProductionFiles()
    {
        var root = RepoRoot();
        foreach (var dir in new[] { "libs", "services", "tools" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                    relative.Contains("/bin/", StringComparison.Ordinal) ||
                    relative.Contains("/Tests/", StringComparison.Ordinal)) continue;
                yield return (file, relative);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>
/// 28.7 — the platform normalises an ICD-10 code in exactly ONE place (design 44 §6, §10 invariant 16).
///
/// <para>
/// It did not. <c>MasterDataNormalize.IcdCategory</c> defined the dot-handling, and
/// <c>PrescriptionValidator</c> carried a private copy of the same three lines. Both were correct on the day
/// they were written, which is precisely how a duplicated matching rule survives long enough to diverge —
/// and the divergence surfaces as an indication check disagreeing with the catalogue that fed it, not as a
/// failing test.
/// </para>
///
/// <para>
/// The rule now lives in <c>libs/clinical-codes</c>, which the catalogue and the engine both reference.
/// </para>
/// </summary>
public class OneIcdNormaliserTests
{
    private const string Home = "libs/clinical-codes/IcdCodes.cs";

    [Fact]
    public void Only_one_file_implements_ICD_dot_normalisation()
    {
        // The shape of the rule, wherever it is written: take three characters off an ICD code. Matching on
        // the BEHAVIOUR rather than a method name is deliberate — a copy would not be called IcdCategory.
        var implementations = ProductionFiles()
            .Where(f =>
            {
                var text = File.ReadAllText(f.Absolute);
                return Regex.IsMatch(text, @"Length\s*<=\s*3\s*\?.*\[\.\.3\]", RegexOptions.Singleline)
                    || Regex.IsMatch(text, @"\[\.\.3\].*ToUpperInvariant", RegexOptions.Singleline);
            })
            .Select(f => f.Relative)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        implementations.Should().BeSubsetOf([Home],
            "ICD matching is a hierarchy walk with ONE dot-normalisation (design 44 §10 invariant 16). "
            + "MasterDataNormalize.IcdCategory delegates to {0}; anything else that truncates a code to "
            + "three characters is a second implementation waiting to disagree with the first", Home);
    }

    [Fact]
    public void The_prescribing_engine_does_not_carry_its_own_copy()
    {
        // Named explicitly because this is where the duplicate actually lived, and where a future "avoid the
        // dependency" refactor would put it back.
        var validator = Path.Combine(RepoRoot(), "libs", "clinical-validation", "PrescriptionValidator.cs");
        if (!File.Exists(validator)) return;

        File.ReadAllText(validator).Should().NotContain("private static string IcdCategory",
            "the engine calls IcdCodes.Category — a private copy is how the two normalisations drifted apart");
    }

    private static IEnumerable<(string Absolute, string Relative)> ProductionFiles()
    {
        var root = RepoRoot();
        foreach (var dir in new[] { "libs", "services", "tools" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                    relative.Contains("/bin/", StringComparison.Ordinal) ||
                    relative.Contains("/Tests/", StringComparison.Ordinal)) continue;
                yield return (file, relative);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
