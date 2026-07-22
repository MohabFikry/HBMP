using FluentAssertions;
using Mersal.Audit.Client;

namespace Mersal.Audit.Client.Tests;

public class AuditSnapshotTests
{
    [Fact]
    public void Sensitive_class_values_are_redacted_but_captured_as_field_classes()
    {
        var (json, classes) = AuditSnapshot.Minimize(new Dictionary<string, (object?, string)>
        {
            ["status"] = ("Active", "operational"),
            ["diagnosisCode"] = ("E11.9", "diagnosis"),
            ["nationalId"] = ("29001011234567", "pii"),
        });

        json.Should().Contain("Active");
        json.Should().NotContain("E11.9");            // diagnosis value never stored
        json.Should().NotContain("29001011234567");   // pii value never stored
        json.Should().Contain(AuditSnapshot.Redacted);
        classes.Should().BeEquivalentTo("operational", "diagnosis", "pii");
    }

    [Fact]
    public void Non_sensitive_snapshot_keeps_values()
    {
        var (json, classes) = AuditSnapshot.Minimize(new Dictionary<string, (object?, string)>
        {
            ["fromStatus"] = ("Pending", "operational"),
            ["toStatus"] = ("Active", "operational"),
        });

        json.Should().Contain("Pending").And.Contain("Active");
        classes.Should().ContainSingle().Which.Should().Be("operational");
    }
}
