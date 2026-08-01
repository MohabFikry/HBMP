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
            [new MemberFollowUp(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Cardiology")]);

        var json = JsonSerializer.Serialize(m, Web).ToLowerInvariant();
        foreach (var f in Forbidden)
            json.Should().NotContain(f, because: $"the serialized 360 must not leak a '{f}' token");
        // Sanity: appointments from two DIFFERENT branches are present (MemberScoped / all branches).
        json.Should().Contain("aswan").And.Contain("maadi");
    }

    /// <summary>
    /// Every FREE-TEXT field in the 360 is on an explicit allow-list.
    ///
    /// <para>The two tests above look like a proof and are not one. The first checks property NAMES, so a
    /// clinical field escapes it by being called something neutral. The second serializes an instance this test
    /// populates itself, so it only ever proves that the values chosen here are clean — a free-text field passes
    /// forever as long as the author picks a benign string for it.</para>
    ///
    /// <para>That is not hypothetical: <c>MemberFollowUp.Reason</c> carried emr's follow-up reason verbatim —
    /// where "review biopsy result" lives — through both tests, green, for as long as it existed. A string
    /// arriving from a sibling service is clinical or not depending on what that sibling wrote in it, which is
    /// not knowable from here. So the rule is inverted: a new string field FAILS until someone adds it below
    /// and, in doing so, states what it holds and why the call centre may see it.</para>
    /// </summary>
    [Fact]
    public void Every_free_text_field_in_the_360_is_explicitly_allow_listed()
    {
        // Logistics, identity and benefit fields only. Each is either an enum-like code, a name the agent needs
        // to run the call, or a bounded identifier — never a field a clinician writes prose into.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "MemberNo", "DisplayName", "AgeBand", "Status",        // identity header
            "Label", "Icon", "Shape", "Tone",                      // StatusCue — non-colour status semantics
            "Category",                                            // coverage line
            "Kind", "Value", "PreferredChannel",                   // contact point
            "AppointmentType", "BranchName", "DoctorName", "Specialty",
            "ReferralRef", "RequestedSpecialty",
        };

        var stringFields = StringPropertyNames(typeof(Member360), []).ToHashSet(StringComparer.Ordinal);

        stringFields.Except(allowed).Should().BeEmpty(
            because: "a new free-text field on the Call Centre 360 must be reviewed and allow-listed here — "
                   + "a string from a sibling service holds whatever that service chose to write in it");
    }

    private static IEnumerable<string> StringPropertyNames(Type t, HashSet<Type> seen)
    {
        if (!seen.Add(t)) yield break;
        foreach (var p in t.GetProperties())
        {
            var pt = p.PropertyType;
            if (pt == typeof(string)) { yield return p.Name; continue; }
            if (pt.IsGenericType) pt = pt.GetGenericArguments()[^1];   // unwrap List<T>/IReadOnlyList<T>
            if (pt.Namespace?.StartsWith("Mersal", StringComparison.Ordinal) == true)
                foreach (var n in StringPropertyNames(pt, seen)) yield return n;
        }
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
