using System.Text.Json;
using FluentAssertions;
using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Tests;

/// <summary>The "no clinical data, ever" invariant, proven structurally: the <see cref="Member360"/> graph has no
/// property whose name could carry clinical content, and a fully-populated instance serializes to JSON containing
/// none of the forbidden clinical tokens. This is the min-necessary authorization proof for the Call Centre 360
/// (design 37 §6, 11-permission-matrix — the call centre gets NO clinical data).</summary>
public class MemberProjectionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    // NB: avoid substrings that collide with benign field names (e.g. "lab" ⊂ "Label"); "labresult" stays covered
    // by "result" + "diagnos"/"vital"/etc.
    private static readonly string[] Forbidden =
        ["diagnos", "icd10", "prescription", "medication", "labresult", "clinicalnote", "vital", "examination", "soap", "allerg"];

    [Fact]
    public void Member360_type_graph_has_no_clinical_property()
    {
        var names = PropertyNames(typeof(Member360), []).Select(n => n.ToLowerInvariant()).ToList();
        foreach (var f in Forbidden)
            names.Should().NotContain(n => n.Contains(f, StringComparison.Ordinal),
                because: $"the Call Centre 360 must not expose a '{f}' field");
    }

    [Fact]
    public void Populated_Member360_serializes_without_any_clinical_token()
    {
        var m = new Member360(
            new MemberIdentity(Guid.NewGuid(), "MRS-M-1001", "Amal Hassan", "30-39", "Active", StatusCue.For("Active")),
            [new CoverageLine("Outpatient", 10000m, 7500m)],
            [new MemberContact(Guid.NewGuid(), "Phone", "+20100...", true, "WhatsApp")],
            [
                new MemberAppointment(Guid.NewGuid(), "Consultation", "Scheduled", DateTimeOffset.UtcNow, "Aswan", "Dr. Nour", "Cardiology", true, true),
                new MemberAppointment(Guid.NewGuid(), "Consultation", "Completed", DateTimeOffset.UtcNow.AddDays(-30), "Maadi", "Dr. Sami", "Dermatology", false, false),
            ],
            [new MemberReferral("REF-2026-000007", "Requested", "Endocrinology", DateTimeOffset.UtcNow)],
            [new MemberFollowUp(Guid.NewGuid(), "Review", DateOnly.FromDateTime(DateTime.UtcNow), "Cardiology")]);

        var json = JsonSerializer.Serialize(m, Web).ToLowerInvariant();
        foreach (var f in Forbidden)
            json.Should().NotContain(f, because: $"the serialized 360 must not leak a '{f}' token");
        // Sanity: appointments from two DIFFERENT branches are present (MemberScoped / all branches).
        json.Should().Contain("aswan").And.Contain("maadi");
    }

    private static IEnumerable<string> PropertyNames(Type t, HashSet<Type> seen)
    {
        if (!seen.Add(t)) yield break;
        foreach (var p in t.GetProperties())
        {
            yield return p.Name;
            var pt = p.PropertyType;
            if (pt.IsGenericType) pt = pt.GetGenericArguments()[^1];   // unwrap List<T>/IReadOnlyList<T>
            if (pt.Namespace?.StartsWith("Mersal", StringComparison.Ordinal) == true)
                foreach (var n in PropertyNames(pt, seen)) yield return n;
        }
    }
}
