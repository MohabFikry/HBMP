using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;

namespace Mersal.Authz.Tests;

public class FieldProjectorTests
{
    private readonly InMemoryAuditOutbox _outbox = new();
    private FieldProjector Projector() =>
        new(DefaultPolicies.FieldMatrix(), new AuditClient(_outbox, new AuditClientContext("test"), TimeProvider.System));

    private static HbmpPrincipal With(string role) => new()
    {
        Subject = "u", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(), MfaSatisfied = true,
    };

    private static Dictionary<string, (object?, string)> EmrRecord() => new()
    {
        ["memberNo"] = ("MRS-M-1", DefaultPolicies.Classes.Identity),
        ["coverage"] = ("LAB", DefaultPolicies.Classes.Coverage),
        ["diagnosisCode"] = ("E11.9", DefaultPolicies.Classes.Diagnosis),
        ["note"] = ("patient reports...", DefaultPolicies.Classes.Clinical),
    };

    [Fact]
    public async Task Reception_projection_strips_diagnosis_and_clinical_and_audits()
    {
        var result = await Projector().ProjectAsync(With("reception"), "encounter", EmrRecord());

        result.Should().ContainKey("memberNo");
        result.Should().ContainKey("coverage");
        result.Should().NotContainKey("diagnosisCode"); // min-necessary: reception ≠ diagnosis
        result.Should().NotContainKey("note");

        _outbox.Events.Should().ContainSingle()
            .Which.Should().Match<AuditEvent>(e =>
                e.DecisionOutcome == "field-strip" && e.FieldClasses.Contains("diagnosis"));
    }

    [Fact]
    public async Task Finance_never_receives_diagnosis()
    {
        var result = await Projector().ProjectAsync(With("finance"), "encounter", EmrRecord());
        result.Should().NotContainKey("diagnosisCode"); // hard rule: finance ≠ diagnosis
    }

    [Fact]
    public async Task Treating_doctor_sees_full_clinical_record_no_strip()
    {
        var result = await Projector().ProjectAsync(With("doctor"), "encounter", EmrRecord());

        result.Should().ContainKeys("memberNo", "coverage", "diagnosisCode", "note");
        _outbox.Events.Should().BeEmpty(); // nothing stripped → no field-strip audit
    }

    [Fact]
    public async Task Lab_does_not_receive_prescription_class()
    {
        var rx = new Dictionary<string, (object?, string)>
        {
            ["drug"] = ("Amoxicillin", DefaultPolicies.Classes.Prescription),
            ["labResult"] = ("value", DefaultPolicies.Classes.Result),
        };

        var result = await Projector().ProjectAsync(With("lab_tech"), "record", rx);

        result.Should().ContainKey("labResult");
        result.Should().NotContainKey("drug"); // labs ≠ prescriptions
    }
}
