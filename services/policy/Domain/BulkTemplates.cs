using System.Globalization;

namespace Mersal.Policy.Domain;

// Phase 19.5b — the column contract. Each job type declares its columns EXPLICITLY, and the parser refuses a
// file that does not match.
//
// ============================================================================================================
// WHY AN UNKNOWN COLUMN FAILS THE WHOLE JOB
// ============================================================================================================
// The tempting behaviour is to ignore columns we do not recognise and get on with it. That is how a file with
// `effective_date` where the template says `effective_from` imports ten thousand memberships starting today
// instead of in January — every row "valid", nothing to see in the report. A column the engine cannot place is
// a statement that the operator and the system disagree about what the file means, and the only safe reading
// of that disagreement is to stop before anything is applied.

/// <summary>How a column's value is read. Kept small on purpose — a bulk template is a data-entry contract for
/// people working in a spreadsheet, not a type system.</summary>
public enum BulkColumnKind { Text, Date, WholeNumber, Number, Boolean, Identifier }

public sealed record BulkColumn(string Name, BulkColumnKind Kind, bool Required, string DescriptionEn, string DescriptionAr)
{
    /// <summary>Header matching is case- and separator-insensitive: <c>Effective From</c>, <c>effective_from</c>
    /// and <c>EFFECTIVE-FROM</c> are the same column. Spreadsheets are edited by people, and rejecting a file
    /// over a capital letter teaches operators to fight the tool rather than read its errors.</summary>
    public static string Canonical(string header) =>
        new([.. (header ?? "").Trim().ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    public string CanonicalName => Canonical(Name);
}

public sealed record BulkTemplate(BulkJobType JobType, IReadOnlyList<BulkColumn> Columns, string PurposeEn, string PurposeAr)
{
    public IEnumerable<BulkColumn> RequiredColumns => Columns.Where(c => c.Required);

    /// <summary>The downloadable template: the header row, plus a commented legend of which columns are
    /// required. An empty file with the right headers is the single most effective way to prevent the column
    /// mismatch above.</summary>
    public string ToCsv()
    {
        var header = string.Join(',', Columns.Select(c => c.Name));
        var legend = string.Join(',', Columns.Select(c => c.Required ? "required" : "optional"));
        var kinds = string.Join(',', Columns.Select(c => c.Kind.ToString().ToLowerInvariant()));
        return string.Create(CultureInfo.InvariantCulture, $"{header}\n# {legend}\n# {kinds}\n");
    }
}

public static class BulkTemplates
{
    private static readonly BulkColumn MemberNo =
        new("member_no", BulkColumnKind.Text, true, "The member number.", "رقم العضوية.");
    private static readonly BulkColumn Reason =
        new("reason", BulkColumnKind.Text, true, "Why this change is being made.", "سبب هذا التغيير.");

    /// <summary>
    /// The member intake file.
    ///
    /// <para>Keyed on <c>card_number</c> rather than an internal id. The operator building this file works
    /// from cards and case papers and has never seen a <c>beneficiary_id</c> — asking for one meant every
    /// intake had to be preceded by a lookup pass that produced the very ids the file was meant to create.
    /// The card number is the business key the whole record already turns on, and it is what makes a
    /// RE-UPLOAD safe: the same card is the same person, so a corrected file updates rather than duplicates.</para>
    ///
    /// <para>Notably absent: <c>age</c>. It is derived from <c>birthdate</c> at read time, everywhere. A file
    /// that carried both would eventually carry two different answers, and there is no rule that could say
    /// which one to believe.</para>
    /// </summary>
    public static readonly BulkTemplate MemberEnrolment = new(BulkJobType.MemberEnrolment,
    [
        new("card_number", BulkColumnKind.Text, true, "The number on the member's card. The key this file matches on — re-uploading updates the same person.", "الرقم المدوّن على بطاقة العضو. المفتاح الذي يطابق عليه هذا الملف — إعادة الرفع تُحدّث الشخص نفسه."),
        // Optional, and the fallback is Pending — the same state the registration form produces. A migration
        // of historical members may state Active; a file of new arrivals should not have to.
        new("status", BulkColumnKind.Text, false, "Active, Suspended or Closed; blank leaves the member Pending for approval.", "نشط، موقوف، أو مغلق؛ الفراغ يترك العضو قيد الاعتماد."),
        new("first_name", BulkColumnKind.Text, true, "Given name.", "الاسم الأول."),
        new("middle_name", BulkColumnKind.Text, false, "Middle name.", "الاسم الأوسط."),
        new("last_name", BulkColumnKind.Text, true, "Family name.", "اسم العائلة."),
        new("plan", BulkColumnKind.Text, true, "The plan to elect — Mersal, UNCR Direct Billing or UNCR Cash Reimbursement.", "الخطة المختارة — مرسال، أو الفوترة المباشرة، أو التعويض النقدي."),
        new("network_tier", BulkColumnKind.Text, true, "Mersal, UNCR, Comprehensive or Restricted network.", "شبكة مرسال، أو المفوضية، أو الشاملة، أو المقيّدة."),
        new("contribution", BulkColumnKind.Number, true, "The member's share of the service price, as a percentage.", "نسبة مشاركة العضو في تكلفة الخدمة."),
        new("default_branch", BulkColumnKind.Text, false, "The internal clinic this member is normally seen at.", "العيادة الداخلية التي يتابَع بها العضو عادة."),
        new("individual_no", BulkColumnKind.Text, false, "The programme's individual reference.", "الرقم الفردي في البرنامج."),
        new("case_no", BulkColumnKind.Text, false, "The case file this member belongs to.", "رقم الحالة التابع لها العضو."),
        new("gender", BulkColumnKind.Text, true, "Male, Female, Other or Unknown.", "ذكر، أنثى، آخر، أو غير معروف."),
        new("nationality", BulkColumnKind.Text, true, "ISO 3166-1 alpha-2 country code, e.g. SY.", "رمز الدولة حسب ISO 3166-1، مثل SY."),
        new("phone_no", BulkColumnKind.Text, true, "Country code and number, e.g. +201234567890.", "رمز الدولة والرقم، مثل ‎+201234567890."),
        new("birthdate", BulkColumnKind.Date, true, "Date of birth (yyyy-MM-dd). Age is calculated from it and must not be sent.", "تاريخ الميلاد (سنة-شهر-يوم). يُحتسب العمر منه ولا يُرسَل."),
        new("effective_from", BulkColumnKind.Date, false, "First day of cover; blank = the day the file is committed.", "أول يوم تغطية؛ الفراغ = يوم تنفيذ الملف."),
        new("note_1", BulkColumnKind.Text, false, "Known diagnosis.", "التشخيص المعروف."),
        new("note_2", BulkColumnKind.Text, false, "Forecasted case cost.", "التكلفة المتوقعة للحالة."),
        new("note_3", BulkColumnKind.Text, false, "Insulin patient.", "مريض أنسولين."),
        new("note_4", BulkColumnKind.Text, false, "Most visited speciality.", "التخصص الأكثر زيارة."),
        new("note_5", BulkColumnKind.Text, false, "Free note.", "ملاحظة حرة."),
        new("note_6", BulkColumnKind.Text, false, "Free note.", "ملاحظة حرة."),
    ], "Register and enrol members.", "تسجيل الأعضاء وقيدهم.");

    public static readonly BulkTemplate MemberTermination = new(BulkJobType.MemberTermination,
    [
        MemberNo,
        new("effective_date", BulkColumnKind.Date, true, "Last covered day, inclusive.", "آخر يوم تغطية شاملًا."),
        Reason,
    ], "End memberships.", "إنهاء عضويات.");

    public static readonly BulkTemplate PlanChange = new(BulkJobType.PlanChange,
    [
        MemberNo,
        new("plan_label", BulkColumnKind.Text, true, "The plan to move onto.", "الخطة المراد الانتقال إليها."),
        new("effective_date", BulkColumnKind.Date, true, "First day on the new plan.", "أول يوم على الخطة الجديدة."),
        Reason,
    ], "Move members between plans of the same policy.", "نقل أعضاء بين خطط نفس الوثيقة.");

    public static readonly BulkTemplate GroupAssignment = new(BulkJobType.GroupAssignment,
    [
        MemberNo,
        // Blank means REMOVE. Stated on the template because "leave it empty to take them out of the group" is
        // otherwise indistinguishable from "I did not fill this in".
        new("group_code", BulkColumnKind.Text, false, "The group to move into; blank REMOVES them from their group.", "المجموعة الجديدة؛ الفراغ يعني إخراجهم من مجموعتهم."),
        new("effective_date", BulkColumnKind.Date, true, "When the move applies from.", "تاريخ سريان النقل."),
        new("reason", BulkColumnKind.Text, false, "Why.", "السبب."),
    ], "Move members between groups.", "نقل أعضاء بين المجموعات.");

    public static readonly BulkTemplate ContactUpdate = new(BulkJobType.ContactUpdate,
    [
        new("beneficiary_id", BulkColumnKind.Identifier, true, "The beneficiary.", "المستفيد."),
        new("contact_type", BulkColumnKind.Text, true, "Phone, Email, Address or EmergencyContact.", "هاتف، بريد إلكتروني، عنوان، جهة اتصال للطوارئ."),
        new("value", BulkColumnKind.Text, true, "The contact detail.", "بيانات الاتصال."),
        new("is_primary", BulkColumnKind.Boolean, false, "Make this the primary contact of its type.", "جعلها جهة الاتصال الأساسية من نوعها."),
        new("preferred_channel", BulkColumnKind.Text, false, "Preferred channel for notifications.", "القناة المفضلة للإشعارات."),
    ], "Update beneficiary contact details.", "تحديث بيانات الاتصال بالمستفيدين.");

    public static readonly BulkTemplate ProviderTierAssignment = new(BulkJobType.ProviderTierAssignment,
    [
        new("tier_code", BulkColumnKind.Text, true, "The network tier's code.", "رمز شريحة الشبكة."),
        new("scope_type", BulkColumnKind.Text, true, "Provider, Location or ContractLine.", "مقدم خدمة، موقع، أو بند تعاقد."),
        new("scope_id", BulkColumnKind.Identifier, true, "The id of the provider, location or contract line.", "معرّف مقدم الخدمة أو الموقع أو بند التعاقد."),
        new("effective_from", BulkColumnKind.Date, true, "First day the tier applies.", "أول يوم تسري فيه الشريحة."),
        // EXCLUSIVE, because provider-service's assignment window is half-open (19.1b) and this file feeds it
        // directly. Stating the convention on the template is the only place an operator will ever read it.
        new("effective_to", BulkColumnKind.Date, false, "First day NOT covered (exclusive); blank = open-ended.", "أول يوم غير مشمول (غير شامل)؛ الفراغ = مفتوح."),
    ], "Assign providers to network tiers.", "إسناد مقدمي الخدمة إلى شرائح الشبكة.");

    public static readonly BulkTemplate BenefitRuleImport = new(BulkJobType.BenefitRuleImport,
    [
        new("plan_version_id", BulkColumnKind.Identifier, true, "The DRAFT plan version to populate.", "نسخة الخطة (مسودة) المراد تعبئتها."),
        new("benefit_category_code", BulkColumnKind.Text, true, "The benefit category's code.", "رمز فئة المنفعة."),
        new("is_covered", BulkColumnKind.Boolean, true, "Whether the category is covered.", "هل الفئة مغطاة."),
        new("limit_type", BulkColumnKind.Text, false, "Annual, PerEncounter, Lifetime or Count.", "سنوي، لكل زيارة، مدى الحياة، أو عدد."),
        new("limit_value", BulkColumnKind.Number, false, "The ceiling; blank = unlimited.", "الحد الأقصى؛ الفراغ = بلا حد."),
        new("reset_period", BulkColumnKind.Text, false, "None, Monthly, Quarterly or Yearly.", "بدون، شهري، ربع سنوي، أو سنوي."),
        new("waiting_period_days", BulkColumnKind.WholeNumber, false, "Days before the benefit becomes payable.", "عدد أيام الانتظار قبل استحقاق المنفعة."),
        new("requires_preauth", BulkColumnKind.Boolean, false, "Whether pre-authorization is required.", "هل يلزم تصريح مسبق."),
        new("exclusions", BulkColumnKind.Text, false, "Semicolon-separated exclusion codes.", "رموز الاستثناءات مفصولة بفاصلة منقوطة."),
    ], "Populate a draft plan version's benefit rules.", "تعبئة قواعد المنافع لنسخة خطة مسودة.");

    public static BulkTemplate For(BulkJobType type) => type switch
    {
        BulkJobType.MemberEnrolment => MemberEnrolment,
        BulkJobType.MemberTermination => MemberTermination,
        BulkJobType.PlanChange => PlanChange,
        BulkJobType.GroupAssignment => GroupAssignment,
        BulkJobType.ContactUpdate => ContactUpdate,
        BulkJobType.ProviderTierAssignment => ProviderTierAssignment,
        BulkJobType.BenefitRuleImport => BenefitRuleImport,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static IReadOnlyList<BulkTemplate> All =>
    [
        MemberEnrolment, MemberTermination, PlanChange, GroupAssignment,
        ContactUpdate, ProviderTierAssignment, BenefitRuleImport,
    ];
}

/// <summary>What the parser produced from one line: the raw cells keyed by CANONICAL column name.</summary>
public sealed record ParsedRow(int RowNumber, IReadOnlyDictionary<string, string?> Cells)
{
    public string? Text(string column)
    {
        var value = Cells.GetValueOrDefault(BulkColumn.Canonical(column));
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>A whole-file rejection. Distinct from row errors: this is the file being unusable, so no row is
/// evaluated and nothing is written.</summary>
public sealed record ParseFailure(string Code, string DetailEn, string DetailAr);

public sealed record ParseResult(IReadOnlyList<ParsedRow> Rows, ParseFailure? Failure)
{
    public bool Ok => Failure is null;
    public static ParseResult Failed(ParseFailure failure) => new([], failure);
}

/// <summary>
/// Header validation, shared by both parsers. A missing REQUIRED column and an unrecognised column are both
/// whole-file failures, and they are reported together — an operator fixing a header should not discover the
/// second problem only after fixing the first.
/// </summary>
public static class BulkHeaderContract
{
    public static ParseFailure? Check(BulkTemplate template, IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(headers);

        var present = headers.Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(BulkColumn.Canonical).ToHashSet(StringComparer.Ordinal);
        var known = template.Columns.Select(c => c.CanonicalName).ToHashSet(StringComparer.Ordinal);

        var missing = template.RequiredColumns.Where(c => !present.Contains(c.CanonicalName)).Select(c => c.Name).ToList();
        var unknown = headers.Where(h => !string.IsNullOrWhiteSpace(h))
            .Where(h => !known.Contains(BulkColumn.Canonical(h))).ToList();

        if (missing.Count == 0 && unknown.Count == 0) return null;

        var parts = new List<string>();
        var partsAr = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"missing required column(s): {string.Join(", ", missing)}");
            partsAr.Add($"أعمدة مطلوبة مفقودة: {string.Join("، ", missing)}");
        }
        if (unknown.Count > 0)
        {
            parts.Add($"unrecognised column(s): {string.Join(", ", unknown)}");
            partsAr.Add($"أعمدة غير معروفة: {string.Join("، ", unknown)}");
        }

        return new ParseFailure("COLUMN_CONTRACT",
            $"The file does not match the {template.JobType} template — {string.Join("; ", parts)}. " +
            "Download the template and re-upload; no row was read.",
            $"لا يتطابق الملف مع قالب {template.JobType} — {string.Join("؛ ", partsAr)}. " +
            "حمّل القالب وأعد الرفع؛ لم تتم قراءة أي صف.");
    }
}

/// <summary>Cell parsing with the SAME rules for CSV and XLSX. A date that reads as 3 January from a
/// spreadsheet and 1 March from a CSV is the kind of defect that only shows up in production.</summary>
public static class BulkCells
{
    public static bool TryDate(string? raw, out DateOnly value) =>
        DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
        // A spreadsheet cell frequently arrives as a serial-formatted date; accept the round-trip form too, but
        // never a locale-dependent one — dd/mm and mm/dd are indistinguishable and silently wrong.
        || DateOnly.TryParseExact(raw?.Trim(), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    public static bool TryInt(string? raw, out int value) =>
        int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryDecimal(string? raw, out decimal value) =>
        decimal.TryParse(raw?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public static bool TryGuid(string? raw, out Guid value) => Guid.TryParse(raw?.Trim(), out value);

    /// <summary>Accepts the words people actually type. "1"/"0" included because a spreadsheet turns a checkbox
    /// into one of those without asking.</summary>
    public static bool TryBool(string? raw, out bool value)
    {
        value = false;
        var v = raw?.Trim().ToLowerInvariant();
        switch (v)
        {
            case "true" or "yes" or "y" or "1" or "نعم": value = true; return true;
            case "false" or "no" or "n" or "0" or "لا": value = false; return true;
            default: return false;
        }
    }
}
