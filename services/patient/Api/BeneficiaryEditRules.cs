using System.Globalization;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Api;

/// <summary>The identity fields an officer may correct after registration. Every property is optional and
/// absent means "leave alone" — `null` is not a way to clear a field, because "do not touch" and "empty this"
/// must not be the same wire value on a partial update.</summary>
public sealed record BeneficiaryEdit(
    string? GivenName,
    string? MiddleName,
    string? FamilyName,
    DateOnly? BirthDate,
    bool? BirthDateIsApproximate,
    string? Sex,
    string? NationalityCode,
    string? IndividualNo,
    string? CaseNo);

/// <summary>One field that actually moved, and what it moved between. The unit the audit trail records.</summary>
public sealed record FieldChange(string Field, string? Before, string? After);

/// <summary>
/// Pure rules for a beneficiary correction: what is valid, what actually changed, and how to describe it.
///
/// <para>Separated from the endpoint so the interesting decisions — a future birth date is refused, an
/// unchanged value is not a change, a whitespace-only name is not a name — are unit-testable without a
/// database or an HTTP pipeline.</para>
/// </summary>
public static class BeneficiaryEditRules
{
    private static readonly string[] Sexes = ["Male", "Female", "Other", "Unknown"];

    /// <summary>Field-named validation errors, or empty. Mirrors what the registrar enforces at intake, so a
    /// value that could not be registered cannot be edited in either.</summary>
    public static IReadOnlyList<string> Validate(BeneficiaryEdit req, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(req);
        var errors = new List<string>();

        // A name PRESENT but blank is a mistake, not a clearing: these two are mandatory on the record and
        // an update that emptied one would leave a person the directory cannot find.
        if (req.GivenName is not null && string.IsNullOrWhiteSpace(req.GivenName)) errors.Add("givenName");
        if (req.FamilyName is not null && string.IsNullOrWhiteSpace(req.FamilyName)) errors.Add("familyName");

        // The same rule the register form applies: a birth date in the future is a transcription error, and
        // it silently breaks every age-banded eligibility rule that reads it.
        if (req.BirthDate is { } dob && dob > DateOnly.FromDateTime(now.UtcDateTime)) errors.Add("birthDate");

        if (req.Sex is not null && !Sexes.Contains(req.Sex, StringComparer.Ordinal)) errors.Add("sex");

        // ISO 3166-1 alpha-2, as stored. A three-letter code here would be accepted by the column and then
        // match nothing in every report that joins on it.
        if (req.NationalityCode is not null
            && (req.NationalityCode.Length != 2 || !req.NationalityCode.All(char.IsAsciiLetter)))
            errors.Add("nationalityCode");

        return errors;
    }

    /// <summary>
    /// Apply the request to the entity and return only the fields that MOVED.
    ///
    /// <para>Unchanged values are not changes. Without that the audit trail fills with entries recording that
    /// somebody opened a form and pressed save, and the one entry that matters — a corrected birth date —
    /// becomes as hard to find as it was before there was a trail.</para>
    /// </summary>
    public static IReadOnlyList<FieldChange> Apply(Beneficiary b, BeneficiaryEdit req)
    {
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(req);
        var changes = new List<FieldChange>();

        void Text(string field, string? incoming, Func<string?> read, Action<string?> write)
        {
            if (incoming is null) return;
            // Trimmed on the way in: a trailing space is not a correction anyone made on purpose, and it is
            // the difference between two rows that look identical in every report.
            var next = incoming.Trim();
            var normalized = next.Length == 0 ? null : next;
            var current = read();
            if (string.Equals(current, normalized, StringComparison.Ordinal)) return;
            write(normalized);
            changes.Add(new FieldChange(field, current, normalized));
        }

        Text("givenName", req.GivenName, () => b.GivenName, v => b.GivenName = v ?? b.GivenName);
        Text("middleName", req.MiddleName, () => b.MiddleName, v => b.MiddleName = v);
        Text("familyName", req.FamilyName, () => b.FamilyName, v => b.FamilyName = v ?? b.FamilyName);
        Text("individualNo", req.IndividualNo, () => b.IndividualNo, v => b.IndividualNo = v);
        Text("caseNo", req.CaseNo, () => b.CaseNo, v => b.CaseNo = v);
        Text("sex", req.Sex, () => b.Sex, v => b.Sex = v ?? b.Sex);
        // Stored upper-case so a lookup never depends on how it was typed.
        Text("nationalityCode", req.NationalityCode?.ToUpperInvariant(),
            () => b.NationalityCode, v => b.NationalityCode = v ?? b.NationalityCode);

        if (req.BirthDate is { } dob && b.BirthDate != dob)
        {
            changes.Add(new FieldChange("birthDate", Iso(b.BirthDate), Iso(dob)));
            b.BirthDate = dob;
        }

        if (req.BirthDateIsApproximate is { } approx && b.BirthDateIsApproximate != approx)
        {
            changes.Add(new FieldChange("birthDateIsApproximate",
                b.BirthDateIsApproximate.ToString(), approx.ToString()));
            b.BirthDateIsApproximate = approx;
        }

        return changes;
    }

    /// <summary>One side of the change set as a JSON object, for the audit event's before/after. Keys are
    /// ordered by the caller's field order, so the same correction always serializes identically.</summary>
    public static string Describe(IReadOnlyList<FieldChange> changes, bool before)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var pairs = changes.Select(c =>
            $"{Json(c.Field)}:{Json(before ? c.Before : c.After)}");
        return "{" + string.Join(',', pairs) + "}";
    }

    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    private static string? Iso(DateOnly? d) =>
        d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
