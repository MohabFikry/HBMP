using System.Globalization;

namespace Mersal.Claims.Domain;

/// <summary>Claim business key <c>CLM-YYYY-NNNNNN</c> (0A §3, 22 §10A.1 regex <c>^CLM-\d{4}-\d{6}$</c>).</summary>
public static class ClaimNo
{
    public static string Format(int year, int seq) =>
        $"CLM-{year.ToString("D4", CultureInfo.InvariantCulture)}-{seq.ToString("D6", CultureInfo.InvariantCulture)}";
}

/// <summary>Batch business key <c>BAT-YYYY-NNNNNN</c> (0A §3, 22 §10A.5 regex <c>^BAT-\d{4}-\d{6}$</c>).</summary>
public static class BatchNo
{
    public static string Format(int year, int seq) =>
        $"BAT-{year.ToString("D4", CultureInfo.InvariantCulture)}-{seq.ToString("D6", CultureInfo.InvariantCulture)}";
}
