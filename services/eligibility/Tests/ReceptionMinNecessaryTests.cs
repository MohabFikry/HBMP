using System.Reflection;
using FluentAssertions;
using Mersal.Eligibility.Api;
using Mersal.Eligibility.Infrastructure;

namespace Mersal.Eligibility.Tests;

/// <summary>
/// Authorization test (11-permission-matrix, CLAUDE.md § min-necessary): the reception result card and
/// its index document must NOT be able to carry any clinical/EMR field. Because projection happens on
/// the server into these types, an EMR field cannot be requested or received via query manipulation.
/// </summary>
public class ReceptionMinNecessaryTests
{
    // Substrings that would indicate a leak of EMR/clinical data into the reception surface.
    private static readonly string[] EmrTerms =
        ["diagnos", "icd", "note", "prescription", "medication", "drug", "order",
         "result", "vital", "observation", "soap", "clinical", "allerg", "symptom", "labresult"];

    private static IEnumerable<string> PropertyNames(Type t, HashSet<Type>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(t)) yield break;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return p.Name;
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (pt.IsGenericType) pt = pt.GetGenericArguments()[^1];
            if (pt.Namespace?.StartsWith("Mersal", StringComparison.Ordinal) == true)
                foreach (var n in PropertyNames(pt, seen)) yield return n;
        }
    }

    [Theory]
    [InlineData(typeof(ReceptionResultCard))]
    [InlineData(typeof(ReceptionDocument))]
    public void Reception_types_carry_no_emr_fields(Type t)
    {
        var names = PropertyNames(t).ToList();
        foreach (var name in names)
        {
            var lower = name.ToLowerInvariant();
            EmrTerms.Should().NotContain(term => lower.Contains(term, StringComparison.Ordinal),
                $"reception field '{name}' must not expose EMR/clinical data");
        }
    }

    [Fact]
    public void Reception_card_exposes_the_expected_min_necessary_surface()
    {
        var names = PropertyNames(typeof(ReceptionResultCard)).ToList();
        names.Should().Contain(["Identity", "Coverage", "RemainingLimits", "VisitHistory"]);
        names.Should().Contain(["MemberNo", "DisplayName", "Status"]);      // identity
        names.Should().Contain(["Count", "LastVisitDate", "LastVisitType"]); // summary only
    }
}
