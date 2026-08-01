namespace Mersal.Reporting.Domain;

/// <summary>
/// The column-header vocabulary of the analytics tables, authored once in both languages.
///
/// <para>Audit §3.1: the headers were the last monolingual text on the dashboard. Every chart carries an
/// accessible table — the element that exists FOR the reader who cannot see the bars — and in Arabic that
/// table's header row read MOVEMENT / MEMBERS while its title, its row labels and its summary sentence were
/// all Arabic. The one part of the alternative that names what each number IS was the part not translated.</para>
///
/// <para>Named constants rather than literals at each call site, because the same word appears in many series:
/// "Members" heads five of them, "Net payable" three. Written inline they would have been five and three
/// independent chances to author a different Arabic word for one column, and a reader comparing two cards
/// would have no way to know the two headers meant the same thing.</para>
///
/// <para>Point labels are NOT here. They come from the data — a benefit-category code, a network tier, a plan
/// name out of the dimension feed — and inventing bilingual constants for values the database supplies is how
/// a label stops matching the row it names.</para>
/// </summary>
public static class AnalyticsColumns
{
    // ── What is being counted or grouped (the row header of each table) ──────────────────────────────────

    public static readonly BiText Movement = new("Movement", "الحركة");
    public static readonly BiText Relationship = new("Relationship", "صلة القرابة");
    public static readonly BiText Plan = new("Plan", "الخطة");
    public static readonly BiText Population = new("Population", "الفئة");
    public static readonly BiText BenefitCategory = new("Benefit category", "فئة المنفعة");
    public static readonly BiText Band = new("Band", "الشريحة");
    public static readonly BiText Threshold = new("Threshold", "العتبة");
    public static readonly BiText Network = new("Network", "الشبكة");
    public static readonly BiText Payer = new("Payer", "الجهة الممولة");
    public static readonly BiText Tier = new("Tier", "شريحة الشبكة");
    public static readonly BiText Provider = new("Provider", "مقدّم الخدمة");
    public static readonly BiText Metric = new("Metric", "المؤشر");
    public static readonly BiText Measure = new("Measure", "المقياس");
    public static readonly BiText Outlier = new("Outlier", "القيمة الشاذة");
    public static readonly BiText Finding = new("Finding", "الملاحظة");

    // ── The figures ─────────────────────────────────────────────────────────────────────────────────────

    public static readonly BiText Members = new("Members", "الأعضاء");
    public static readonly BiText ActiveMembers = new("Active members", "الأعضاء النشطون");
    public static readonly BiText Count = new("Count", "العدد");
    public static readonly BiText Value = new("Value", "القيمة");
    public static readonly BiText Amount = new("Amount", "المبلغ");
    public static readonly BiText Consumed = new("Consumed", "المستهلك");
    public static readonly BiText Limit = new("Limit", "الحد");
    public static readonly BiText NetPayable = new("Net payable", "صافي المستحق");
    public static readonly BiText Claimed = new("Claimed", "المطالَب به");
    public static readonly BiText Claims = new("Claims", "المطالبات");
    public static readonly BiText CostPerMember = new("Cost per member", "التكلفة لكل عضو");
    public static readonly BiText TotalNet = new("Total net", "إجمالي الصافي");
    public static readonly BiText PercentOfLimitUsed = new("% of limit used", "٪ من الحد المستخدم");
    public static readonly BiText PercentOutOfNetwork = new("% out of network", "٪ خارج الشبكة");
}
