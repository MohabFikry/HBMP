namespace Mersal.Validity;

/// <summary>
/// The kinds of document that carry an expiry the platform cares about (ADR-0035 §6).
/// </summary>
/// <remarks>
/// <para>
/// One vocabulary covering both families the review selected — beneficiary identity and provider credentials
/// — rather than a policy per family. They ask the same two questions ("how long is one of these good for"
/// and "how early do we warn"), and two tables answering the same question is how the two answers drift.
/// </para>
/// <para>
/// A closed enum, not free text. A document kind nothing reads is a policy nobody applies, and an operator
/// who invents "Refugee-Card " with a trailing space would silently get the default for ever.
/// </para>
/// </remarks>
public enum DocumentKind
{
    // ---- Beneficiary identity. A lapse here is what stops somebody being seen at reception. ----
    //
    // These names track `patient.beneficiary_identifier.identifier_type`, whose CHECK constraint is the real
    // vocabulary: NationalID, Passport, RefugeeID, UNHCRNo, MemberNo. An enum that invented its own names
    // would produce a policy that reads beautifully on the supervisor's screen and matches no row in the
    // database — configured, saved, audited, and applying to nothing.
    //
    // MemberNo is deliberately absent: it is issued by Mersal and does not lapse on anybody else's timetable,
    // so a renewal cadence for it would be a number with nothing to be about.
    NationalId,
    Passport,
    RefugeeId,
    UnhcrNo,

    // ---- Provider credentials. A lapse here is what should stop somebody practising. ----
    PractitionerLicence,
    FacilityAccreditation,
    ProviderContract,
}

/// <summary>
/// How long a document is good for, and how early its expiry is warned about.
/// </summary>
/// <remarks>
/// <para>
/// The deliberate sibling of <see cref="ValidityPolicy"/>, sharing its storage (<c>admin.system_config</c>),
/// its fallback discipline and its bounds discipline. A second shape for the same idea would be exactly the
/// drift these libraries exist to prevent.
/// </para>
/// <para>
/// <b>Two numbers, because they answer different questions.</b> <c>days</c> is a renewal cadence — how long
/// this kind of document is expected to stay current after it is issued. <c>warn-days</c> is when somebody is
/// told it is about to lapse. Today the second of those is the hard-coded constant
/// <c>PractitionerLicence.WarningDays = [90, 60, 30]</c>, which means the one number a supervisor most
/// obviously owns is the one they cannot touch.
/// </para>
/// <para>
/// <b>What <c>days</c> is NOT.</b> It does not override a real expiry printed on a document. Mersal does not
/// decide when a government-issued card lapses. It is the cadence used to derive a review date when no expiry
/// was recorded, and anything derived that way is marked as derived — a policy-computed date presented as the
/// document's actual expiry would be the platform inventing a fact about a refugee's papers.
/// </para>
/// </remarks>
public static class DocumentValidityPolicy
{
    /// <summary>
    /// The cadence in force when nobody has set one: one year.
    ///
    /// <para>A CONSTANT, not a nullable "unset", for the same reason <see cref="ValidityPolicy.DefaultDays"/>
    /// is: every path that fails to read configuration must fall back to a real number and never to "no
    /// expiry". A document that can never lapse is the state this exists to prevent, so a config outage, a
    /// missing row, a new tenant or a typo in a key must not be able to produce one.</para>
    /// </summary>
    public const int DefaultDays = 365;

    /// <summary>Floor of 1: a cadence of zero would treat every document as lapsed the day it was recorded.</summary>
    public const int MinDays = 1;

    /// <summary>
    /// Ceiling of ten years.
    ///
    /// <para>Longer than <see cref="ValidityPolicy.MaxDays"/> on purpose — a passport really does run ten
    /// years, where a prescription running one is already a formality rather than a safety control. Still
    /// bounded: a supervisor who types 36500 should be stopped rather than quietly given a century.</para>
    /// </summary>
    public const int MaxDays = 3650;

    /// <summary>
    /// The default warning thresholds: 90, 60 and 30 days out.
    ///
    /// <para>Exactly the constant this replaces (<c>PractitionerLicence.WarningDays</c>), so switching to
    /// configuration changes who may set the number and changes nothing about what happens by default. A
    /// migration that also moved the behaviour would make any later surprise impossible to attribute.</para>
    /// </summary>
    public static IReadOnlyList<int> DefaultWarnDays { get; } = [90, 60, 30];

    /// <summary>The <c>system_config</c> key holding this kind's renewal cadence, in days.</summary>
    public static string KeyFor(DocumentKind kind) => $"document-validity.{Slug(kind)}.days";

    /// <summary>The <c>system_config</c> key holding this kind's warning thresholds, in days.</summary>
    public static string WarnKeyFor(DocumentKind kind) => $"document-validity.{Slug(kind)}.warn-days";

    private static string Slug(DocumentKind kind) => kind switch
    {
        DocumentKind.NationalId => "national-id",
        DocumentKind.Passport => "passport",
        DocumentKind.RefugeeId => "refugee-id",
        DocumentKind.UnhcrNo => "unhcr-no",
        DocumentKind.PractitionerLicence => "practitioner-licence",
        DocumentKind.FacilityAccreditation => "facility-accreditation",
        DocumentKind.ProviderContract => "provider-contract",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown document kind."),
    };

    /// <summary>Every kind, for the supervisor screen and the read endpoint.</summary>
    public static IReadOnlyList<DocumentKind> All { get; } =
    [
        DocumentKind.NationalId, DocumentKind.Passport, DocumentKind.RefugeeId, DocumentKind.UnhcrNo,
        DocumentKind.PractitionerLicence, DocumentKind.FacilityAccreditation, DocumentKind.ProviderContract,
    ];

    /// <summary>Identity documents — the ones whose lapse blocks a beneficiary rather than a provider.</summary>
    public static IReadOnlySet<DocumentKind> IdentityKinds { get; } = new HashSet<DocumentKind>
    {
        DocumentKind.NationalId, DocumentKind.Passport, DocumentKind.RefugeeId, DocumentKind.UnhcrNo,
    };

    /// <summary>
    /// The <c>identifier_type</c> a kind corresponds to, or null when the kind is not an identity document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tie between this policy and the rows it governs, made explicit and testable. Without it the two
    /// vocabularies drift silently: the database's CHECK spells it <c>RefugeeID</c> and an enum member spelled
    /// <c>RefugeeCard</c> would give a supervisor a setting that matches no row — configured, saved, audited,
    /// and applying to nothing at all.
    /// </para>
    /// <para>
    /// <c>MemberNo</c> maps FROM nothing on purpose: it is issued by Mersal, does not lapse on anybody else's
    /// timetable, and a renewal cadence for it would be a number with nothing to be about.
    /// </para>
    /// </remarks>
    public static string? IdentifierTypeFor(DocumentKind kind) => kind switch
    {
        DocumentKind.NationalId => "NationalID",
        DocumentKind.Passport => "Passport",
        DocumentKind.RefugeeId => "RefugeeID",
        DocumentKind.UnhcrNo => "UNHCRNo",
        _ => null,
    };

    /// <summary>
    /// The kind governing an <c>identifier_type</c>, or null when none does.
    /// </summary>
    /// <remarks>
    /// A switch rather than a search over <see cref="IdentityKinds"/>: <c>FirstOrDefault</c> over an enum
    /// returns the enum's DEFAULT when nothing matches, so a search would answer "NationalId" for an unknown
    /// type — quietly applying the wrong policy instead of admitting there is none.
    /// </remarks>
    public static DocumentKind? KindForIdentifierType(string? identifierType) =>
        identifierType?.ToUpperInvariant() switch
        {
            "NATIONALID" => DocumentKind.NationalId,
            "PASSPORT" => DocumentKind.Passport,
            "REFUGEEID" => DocumentKind.RefugeeId,
            "UNHCRNO" => DocumentKind.UnhcrNo,
            // MemberNo and anything unrecognised: no policy governs it, and saying so beats guessing.
            _ => null,
        };

    public static bool IsInRange(int days) => days >= MinDays && days <= MaxDays;

    /// <summary>
    /// Parse a stored cadence. Anything unparseable or out of range resolves to <see cref="DefaultDays"/>
    /// rather than throwing — a malformed row is an operator error that must not stop a clerk registering
    /// somebody, and must not grant an unbounded validity either.
    /// </summary>
    public static int DaysFrom(string? configuredValue) =>
        int.TryParse(configuredValue, out var d) && IsInRange(d) ? d : DefaultDays;

    /// <summary>
    /// Parse stored warning thresholds — a comma-separated list, e.g. <c>"90,60,30"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A LIST, not one number, because the behaviour being replaced fires at three points and a single
    /// threshold would quietly delete two of them. A credential that warns once at 30 days and then goes
    /// silent is worse than the constant it replaced.
    /// </para>
    /// <para>
    /// Sorted descending and de-duplicated so the caller can rely on the order, and so "30,90,30" means what
    /// its author obviously meant rather than firing twice at the same point. Out-of-range entries are
    /// dropped individually; a list that ends up empty falls back whole, because "warn at no point" is not a
    /// configuration anybody should be able to reach by typing a bad number.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> WarnDaysFrom(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue)) return DefaultWarnDays;

        var parsed = configuredValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var n) ? n : -1)
            .Where(IsInRange)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();

        return parsed.Count == 0 ? DefaultWarnDays : parsed;
    }

    /// <summary>The canonical stored form of a threshold list.</summary>
    public static string WarnDaysToValue(IEnumerable<int> days) =>
        string.Join(",", days.Where(IsInRange).Distinct().OrderByDescending(n => n));

    /// <summary>
    /// Days remaining until <paramref name="expiresOn"/>, or null when no expiry is recorded.
    /// </summary>
    /// <remarks>
    /// Null is <b>unknown</b>, and every caller must render it as unknown rather than as valid. A document
    /// with no recorded expiry is not a document that never expires — it is one nobody has told us about, and
    /// the two must never look the same on a screen.
    /// </remarks>
    public static int? DaysUntil(DateOnly? expiresOn, DateOnly asOf) =>
        expiresOn is null ? null : expiresOn.Value.DayNumber - asOf.DayNumber;

    /// <summary>
    /// The threshold crossed exactly on <paramref name="asOf"/>, or null on any other day.
    /// </summary>
    /// <remarks>
    /// Crossed ON the day, so a daily sweeper emits one notice per threshold rather than one per day for the
    /// remaining ninety — which is how a warning system teaches people to ignore it.
    /// </remarks>
    public static int? ThresholdCrossedOn(DateOnly? expiresOn, DateOnly asOf, IReadOnlyList<int> warnDays)
    {
        var remaining = DaysUntil(expiresOn, asOf);
        return remaining is null ? null : warnDays.Contains(remaining.Value) ? remaining : null;
    }

    /// <summary>
    /// The review date derived from the cadence when no expiry was recorded.
    /// </summary>
    /// <remarks>
    /// <b>Derived, and the caller must say so.</b> This is not the document's expiry — Mersal does not decide
    /// when a government-issued card lapses. It is when somebody should be asked to check. Presenting it as
    /// the real thing would be the platform inventing a fact about a refugee's papers.
    /// </remarks>
    public static DateOnly DerivedReviewDate(DateOnly recordedOn, int cadenceDays) =>
        recordedOn.AddDays(cadenceDays);
}
