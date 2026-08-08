namespace Mersal.MasterData.Domain;

/// <summary>One disagreement between the loaded catalogue and design 45 §2's published ranges.</summary>
/// <param name="Kind">Machine-readable discriminator, so a CI gate can assert on a class of finding.</param>
/// <param name="Detail">What disagrees.</param>
/// <param name="Resolution">What the platform did about it. The RANGE always wins (design 45 §2); this
/// records the consequence so it is a decision on the record rather than an unexplained behaviour.</param>
public sealed record RoutingDiscrepancy(string Kind, string Detail, string Resolution);

/// <summary>How many loaded codes each vehicle claims, plus the discrepancies found.</summary>
public sealed record RoutingReconciliationReport(
    IReadOnlyDictionary<string, int> CodesPerSection,
    IReadOnlyDictionary<string, int> CodesPerVehicle,
    IReadOnlyDictionary<string, int> LoadedCategoryValues,
    IReadOnlyList<RoutingDiscrepancy> Discrepancies)
{
    public int TotalCodes => CodesPerSection.Values.Sum();
}

/// <summary>
/// 29.2 — builds the CPT routing map FROM THE LOADED CATALOGUE and reconciles it against design 45 §2's
/// published ranges, reporting every disagreement rather than silently resolving it.
///
/// <para>The report is emitted as loader/migration output (design 45 §2) so the routing that decides whether
/// a code becomes an order or a referral is inspectable, not implicit in a regex nobody reads.</para>
/// </summary>
public static class CptRoutingReconciliation
{
    /// <summary>Design 45 §2's published ranges, transcribed verbatim so the comparison is against what the
    /// document actually says rather than against a tidied-up recollection of it.</summary>
    private static readonly (string Label, int Low, int High, string Routes)[] PublishedRanges =
    [
        ("Surgery",              10004, 69990, "Procedure order"),
        ("Radiology",            70010, 79999, "Radiology tab"),
        ("Pathology & Lab",      80047, 89398, "Labs tab"),
        ("Medicine",             90281, 99607, "Procedure order"),
        ("Evaluation & Mgmt",    99202, 99499, "Referral"),
    ];

    /// <param name="loadedCodes">Every CPT code in the catalogue, with its loaded <c>category</c> value.</param>
    public static RoutingReconciliationReport Build(IEnumerable<(string Code, string? Category)> loadedCodes)
    {
        ArgumentNullException.ThrowIfNull(loadedCodes);

        var perSection = new Dictionary<string, int>(StringComparer.Ordinal);
        var perVehicle = new Dictionary<string, int>(StringComparer.Ordinal);
        var perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        var outOfPublishedRange = new List<string>();

        foreach (var (code, category) in loadedCodes)
        {
            var decision = CptRouting.For(code);
            perSection[decision.Section] = perSection.GetValueOrDefault(decision.Section) + 1;
            perVehicle[decision.Vehicle.ToString()] = perVehicle.GetValueOrDefault(decision.Vehicle.ToString()) + 1;

            var cat = category ?? "(null)";
            perCategory[cat] = perCategory.GetValueOrDefault(cat) + 1;

            // Codes the platform routes to a clinical vehicle but which fall OUTSIDE every published range.
            if (decision.IsOrderable && int.TryParse(code, out var n)
                && !PublishedRanges.Any(r => n >= r.Low && n <= r.High))
            {
                outOfPublishedRange.Add(code);
            }
        }

        var discrepancies = new List<RoutingDiscrepancy>();

        // ---- 1. The premise itself ------------------------------------------------------------------------
        // Reported first because it invalidates the instruction rather than merely qualifying it, and because
        // anyone reading this report will otherwise wonder why `category` appears nowhere in the routing.
        if (perCategory.Count > 0)
        {
            discrepancies.Add(new RoutingDiscrepancy(
                "category-is-not-the-section",
                $"cpt_code.category holds the CPT TAXONOMY, not the section. Loaded values: "
                + string.Join(", ", perCategory.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value:N0})"))
                + ". These record how a code was adopted into the book, not whether it is a scan, a blood test "
                + "or an office visit.",
                "Routing is derived from the code's NUMERIC RANGE via CptSections, which partitions the "
                + "catalogue with no overlap and no remainder. `category` is not used as a routing input."));
        }

        // ---- 2. Design 45 §2's Medicine and E/M ranges overlap ---------------------------------------------
        var medicine = PublishedRanges.First(r => r.Label == "Medicine");
        var em = PublishedRanges.First(r => r.Label == "Evaluation & Mgmt");
        if (em.Low <= medicine.High && medicine.Low <= em.High)
        {
            discrepancies.Add(new RoutingDiscrepancy(
                "published-ranges-overlap",
                $"Design 45 §2 lists Medicine as {medicine.Low}–{medicine.High} and E/M as {em.Low}–{em.High}; "
                + $"these overlap at {Math.Max(medicine.Low, em.Low)}–{Math.Min(medicine.High, em.High)}. Read "
                + "literally, every office-visit code is BOTH a Procedure order and a Referral.",
                "The overlap resolves to E/M → Referral, which is the section's plain intent: an office visit "
                + "is the referral case design 45 §2 describes at length. A Procedure order would never have "
                + "its loop closed with a report back."));
        }

        // ---- 3. Sections the published table omits ---------------------------------------------------------
        var anesthesia = perSection.GetValueOrDefault(CptSections.Anesthesia);
        if (anesthesia > 0)
        {
            discrepancies.Add(new RoutingDiscrepancy(
                "section-absent-from-published-ranges",
                $"{anesthesia:N0} Anesthesia codes (00100–01999) are loaded, but design 45 §2's table does not "
                + "list the section, while stating that every remaining category is orderable.",
                "Routed NotOrderable WITH A STATED REASON — anesthesia is billed alongside the procedure it "
                + "accompanies, not ordered from an outpatient encounter. Reported rather than omitted: a code "
                + "that simply fails to appear in a picker is indistinguishable from a catalogue gap."));
        }

        var other = perSection.GetValueOrDefault(CptSections.Other);
        if (other > 0)
        {
            discrepancies.Add(new RoutingDiscrepancy(
                "letter-suffixed-codes-outside-the-sectioned-book",
                $"{other:N0} Category II / Category III / PLA / MAAA codes carry a letter suffix and sit "
                + "outside the sectioned body of CPT, so no published range covers them.",
                "Routed NotOrderable with a reason. A performance measure is not a service anyone delivers."));
        }

        // ---- 4. Orderable codes outside every published range ----------------------------------------------
        if (outOfPublishedRange.Count > 0)
        {
            discrepancies.Add(new RoutingDiscrepancy(
                "orderable-code-outside-published-ranges",
                $"{outOfPublishedRange.Count:N0} code(s) route to a clinical vehicle but fall outside every "
                + $"published range — e.g. {string.Join(", ", outOfPublishedRange.Take(10))}. The published "
                + "ranges have gaps at their edges (Surgery starts at 10004, the section at 10000; Medicine "
                + "starts at 90281, the section at 90000).",
                "THE RANGE WINS for classification, but a loaded code is never dropped for sitting in a gap: "
                + "it keeps its section's vehicle. Excluding real catalogue codes to match a range's rounded "
                + "endpoints would remove orderable services from the picker with no clinical justification."));
        }

        return new RoutingReconciliationReport(perSection, perVehicle, perCategory, discrepancies);
    }

    /// <summary>Render the report for loader / migration output.</summary>
    public static string Format(RoutingReconciliationReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== CPT routing reconciliation (29.2, design 45 §2) ===");
        sb.AppendLine($"Codes classified: {r.TotalCodes:N0}");
        sb.AppendLine();
        sb.AppendLine("Per section:");
        foreach (var (k, v) in r.CodesPerSection.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {k,-24} {v,8:N0}");
        sb.AppendLine();
        sb.AppendLine("Per vehicle (what ordering the code CREATES):");
        foreach (var (k, v) in r.CodesPerVehicle.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {k,-24} {v,8:N0}");
        sb.AppendLine();
        sb.AppendLine($"Discrepancies vs design 45 §2 ({r.Discrepancies.Count}) — reported, not silently resolved:");
        foreach (var d in r.Discrepancies)
        {
            sb.AppendLine($"  [{d.Kind}]");
            sb.AppendLine($"    found:      {d.Detail}");
            sb.AppendLine($"    resolution: {d.Resolution}");
        }
        return sb.ToString();
    }
}
