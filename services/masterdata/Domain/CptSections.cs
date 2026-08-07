namespace Mersal.MasterData.Domain;

/// <summary>
/// The CPT <b>section</b> a procedure code belongs to — imaging, laboratory, pathology and the rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>The catalogue does not carry this, and the column that looks like it is a different thing.</b> The
/// source workbook (<c>Master Lists/CPT 2022 Codes.xlsx</c>, 10,810 rows) has three columns: Code, Category
/// and Description — and <c>Category</c> is the CPT <i>taxonomy</i>: Category I (9,584), Category II (565),
/// Category III (383), PLA (265), MAAA (13). That says how a code was adopted into the book, not whether it
/// is a scan or a blood test. Grouping the ordering screens by it would put a chest x-ray and a urine
/// culture in the same bucket and split two chest x-rays across three.
/// </para>
/// <para>
/// <b>The section is carried by the code's own numeric range</b>, which is how the book itself is organised
/// and why CPT codes are assigned in blocks rather than sequentially. The ranges below were verified against
/// the workbook rather than recalled: they partition all 9,584 five-digit numeric codes with no overlap and
/// no remainder, and the 1,226 rows they leave out are exactly the letter-suffixed Category II (F),
/// Category III (T), PLA (U) and MAAA (M) codes, which sit outside the sectioned body of the book and land
/// in <see cref="Other"/>.
/// </para>
/// <para>
/// <b>Why patterns and not a stored column.</b> A section is a pure function of the code, so storing it would
/// add a column that can only ever disagree with the code beside it — and a migration whose backfill is the
/// very expression written here. Kept as one regex per section, composed into a single alternation when a
/// caller asks for several, so the whole filter is one <c>~</c> the database can run.
/// </para>
/// </remarks>
public static class CptSections
{
    /// <summary>Anesthesia — 00100–01999.</summary>
    public const string Anesthesia = "Anesthesia";
    /// <summary>Surgery — 10004–69990. Much the largest section (5,823 of the workbook's codes).</summary>
    public const string Surgery = "Surgery";
    /// <summary>Radiology — 70010–79999. Named for what a clinician orders, not for the book's chapter title.</summary>
    public const string Imaging = "Imaging";
    /// <summary>
    /// The laboratory half of "Pathology and Laboratory" — panels, assays, urinalysis, molecular pathology,
    /// chemistry, haematology, immunology, transfusion medicine and microbiology (80047–87999), plus the
    /// in-vivo and reproductive-medicine procedures at 89049–89398.
    /// </summary>
    public const string Laboratory = "Laboratory";
    /// <summary>
    /// Anatomic pathology proper — necropsy, cytopathology, cytogenetics and surgical pathology (88000–88749).
    /// Split out from <see cref="Laboratory"/> because a specimen sent to a pathologist and a sample run on an
    /// analyser are different work, done by different people, with different turnaround.
    /// </summary>
    public const string Pathology = "Pathology";
    /// <summary>
    /// Medicine — 90281–99199 and 99500–99607: immunisations, dialysis, ophthalmology, cardiovascular and
    /// so on. Includes the anesthesia qualifying-circumstances add-ons at 99100–99140, which are numbered in
    /// this range even though the book discusses them under Anesthesia; separating 41 add-on codes that
    /// nobody orders from a clinical tab would buy nothing.
    /// </summary>
    public const string Medicine = "Medicine";
    /// <summary>Evaluation and Management — 99202–99499, the office and inpatient visit codes.</summary>
    public const string EvaluationAndManagement = "EvaluationAndManagement";
    /// <summary>
    /// The letter-suffixed codes — Category II (performance measures), Category III (emerging technology),
    /// PLA (proprietary lab analyses) and MAAA (multianalyte assays). They are outside the sectioned body of
    /// the book, so they belong to no section rather than being quietly filed under a neighbouring one.
    /// </summary>
    public const string Other = "Other";

    /// <summary>
    /// Section → the regex BODY matching its codes (unanchored, so several can be composed into one
    /// alternation). Mutually exclusive by construction — see the remarks on this class.
    /// </summary>
    private static readonly Dictionary<string, string> Bodies = new(StringComparer.OrdinalIgnoreCase)
    {
        [Anesthesia] = "0[01][0-9]{3}",
        [Surgery] = "[1-6][0-9]{4}",
        [Imaging] = "7[0-9]{4}",
        // 80–87 and 89, with 88 carved out below. `[0-79]` reads oddly and is deliberate: it is "0 through 7,
        // or 9", the second digit of every laboratory code once pathology is removed from the middle.
        [Laboratory] = "8[0-79][0-9]{3}",
        [Pathology] = "88[0-9]{3}",
        [Medicine] = "9[0-8][0-9]{3}|99[015-9][0-9]{2}",
        [EvaluationAndManagement] = "99[2-4][0-9]{2}",
        [Other] = "[0-9]{4}[A-Za-z]",
    };

    public static bool IsKnown(string section) => Bodies.ContainsKey(section);

    /// <summary>
    /// 29.2 — the section a single code belongs to, the inverse of <see cref="PatternFor"/>.
    ///
    /// <para>Needed because <see cref="CptRouting"/> asks "what does ordering THIS code create", which is a
    /// question about one code rather than a filter over many. Evaluated in a fixed order with
    /// <see cref="EvaluationAndManagement"/> and <see cref="Pathology"/> BEFORE the ranges that would
    /// otherwise swallow them: E/M's 99202–99499 sits inside Medicine's 99xxx block, and pathology's 88xxx
    /// inside laboratory's 8xxxx. The <see cref="Bodies"/> patterns are mutually exclusive by construction, so
    /// the order is defensive rather than load-bearing — but the two carve-outs are exactly where a future
    /// edit to one pattern would silently reroute the other, and rerouting E/M turns a referral into a
    /// procedure order that no one ever closes the loop on.</para>
    ///
    /// <para>An unrecognised or absent code returns <see cref="Other"/> — never a clinical section. Guessing
    /// a section for a code the catalogue does not know would put it in a queue on the strength of its digits.</para>
    /// </summary>
    public static string SectionOf(string? code)
    {
        var c = code?.Trim();
        if (string.IsNullOrEmpty(c)) return Other;

        foreach (var section in new[]
                 { EvaluationAndManagement, Pathology, Anesthesia, Surgery, Imaging, Laboratory, Medicine, Other })
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(c, $"^({Bodies[section]})$")) return section;
        }
        return Other;
    }

    /// <summary>
    /// One anchored regex matching the codes of every named section, or <c>null</c> when none is named or
    /// none is recognised — which the caller must read as "do not filter", never as "match nothing".
    /// </summary>
    /// <remarks>
    /// Unknown names are dropped rather than rejected. A section this build has not heard of is a caller
    /// running ahead of a deployment, and answering that with an empty procedure list would look to the
    /// doctor exactly like a catalogue that has no chest x-ray in it.
    /// </remarks>
    public static string? PatternFor(string? sections)
    {
        if (string.IsNullOrWhiteSpace(sections)) return null;
        var bodies = sections
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Bodies.TryGetValue(s, out var body) ? body : null)
            .Where(b => b is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return bodies.Length == 0 ? null : $"^({string.Join('|', bodies)})$";
    }
}
