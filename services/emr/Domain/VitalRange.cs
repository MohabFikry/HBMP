namespace Mersal.Emr.Domain;

/// <summary>Per-type plausibility ranges for vitals (22-data-dictionary §6.5 "range per type"). A value outside
/// the physiological range is rejected at save (US-031 "required field missing OR invalid → save blocked").
/// Ranges are deliberately wide clinical-plausibility bounds, not reference/normal ranges.</summary>
public static class VitalRange
{
    // (min, max, canonical unit) per vital type. BP is captured as systolic here (diastolic rides in Unit/notes).
    private static readonly Dictionary<VitalType, (decimal Min, decimal Max, string Unit)> Ranges = new()
    {
        [VitalType.BP] = (30m, 300m, "mmHg"),
        // The diastolic's plausible band is NOT the systolic's. Sharing one 30–300 range would have accepted
        // a diastolic of 250, which is not a reading a person survives — it is a transposed systolic.
        [VitalType.BPDiastolic] = (20m, 200m, "mmHg"),
        [VitalType.HR] = (20m, 300m, "bpm"),
        [VitalType.Temp] = (25m, 45m, "Cel"),
        [VitalType.SpO2] = (50m, 100m, "%"),
        [VitalType.Weight] = (0.2m, 500m, "kg"),
        [VitalType.Height] = (10m, 260m, "cm"),
        [VitalType.BMI] = (5m, 120m, "kg/m2"),
    };

    /// <summary>Canonical unit for a vital type (used when the caller omits one).</summary>
    public static string CanonicalUnit(VitalType type) => Ranges[type].Unit;

    /// <summary>Validates a numeric vital value; returns an error message when out of range, else null.</summary>
    public static string? Validate(VitalType type, decimal? value)
    {
        if (value is null) return "A numeric value is required for a vital.";
        var (min, max, unit) = Ranges[type];
        return value < min || value > max
            ? $"{type} value {value} is outside the plausible range {min}–{max} {unit}."
            : null;
    }
}
