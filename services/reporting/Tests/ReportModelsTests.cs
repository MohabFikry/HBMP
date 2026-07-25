using FluentAssertions;
using Mersal.Reporting.Domain;

namespace Mersal.Reporting.Tests;

/// <summary>Pure unit tests for the reporting building blocks (US-073): p95 percentile + age bucketing.</summary>
public class ReportModelsTests
{
    [Fact]
    public void P95_uses_nearest_rank()
    {
        var values = Enumerable.Range(1, 100).Select(i => (long)i).ToList();
        Percentile.P95(values).Should().Be(95);
    }

    [Fact]
    public void P95_of_empty_is_zero()
    {
        Percentile.P95([]).Should().Be(0);
    }

    [Theory]
    [InlineData(1, "<4h")]
    [InlineData(10, "4-24h")]
    [InlineData(48, "1-3d")]
    [InlineData(200, ">3d")]
    public void Age_buckets_partition_by_hours(int hours, string bucket)
    {
        AgeBuckets.Of(TimeSpan.FromHours(hours)).Should().Be(bucket);
    }
}
