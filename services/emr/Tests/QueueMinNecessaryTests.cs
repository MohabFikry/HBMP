using System.Reflection;
using FluentAssertions;
using Mersal.Emr.Domain;

namespace Mersal.Emr.Tests;

/// <summary>Minimum-necessary guard (3.3): the reception queue exposes scheduling + display identity ONLY.
/// This reflection test fails the build if any clinical/EMR/PII field is ever added to the queue shape — the
/// same discipline as provider-service's boundary test.</summary>
public class QueueMinNecessaryTests
{
    private static readonly string[] Forbidden =
    [
        "diagnos", "icd", "note", "soap", "prescription", "medication", "drug", "result", "finding",
        "allerg", "vital", "assessment", "plan", "subjective", "objective", "address", "phone",
        "nationalid", "passport",
    ];

    [Fact]
    public void QueueTicket_has_no_clinical_or_pii_fields()
    {
        var offenders = typeof(QueueTicket).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => Forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty("the queue ticket must never carry clinical/EMR/PII data");
    }

    [Fact]
    public void QueueTicket_exposes_only_the_expected_min_necessary_surface()
    {
        var names = typeof(QueueTicket).GetProperties().Select(p => p.Name).ToHashSet();
        names.Should().BeEquivalentTo(new[]
        {
            "QueueId", "AppointmentId", "BeneficiaryId", "ProviderId", "LocationId", "DoctorId",
            "MemberNo", "DisplayName", "AppointmentType", "Priority", "State", "EnqueuedAt", "CalledAt",
        });
    }
}
