using Mersal.Prescribing;

namespace Mersal.Pharmacy.Domain;

/// <summary>Why a chronic collection was refused. <c>None</c> means it may proceed.</summary>
public enum ChronicDispenseError
{
    None,
    /// <summary>Before the window's <c>opens_at</c>. The refusal NAMES the open date.</summary>
    TooEarly,
    /// <summary>The window closed uncollected. Forfeited; it cannot be claimed later.</summary>
    WindowMissed,
    /// <summary>Eligibility failed at the counter. The script is BLOCKED, not cancelled.</summary>
    NotEligible,
    /// <summary>Everything allocated to this window has already been handed over.</summary>
    WindowComplete,
    /// <summary>Past the script's own validity, regardless of the window.</summary>
    ScriptExpired,
}

/// <summary>What the counter should do, and what to tell the pharmacist.</summary>
public sealed record ChronicDispenseDecision(
    ChronicDispenseError Error,
    decimal DispensableQuantity,
    DateOnly? OpensAt = null,
    string? BlockReason = null)
{
    public bool Allowed => Error == ChronicDispenseError.None;

    /// <summary>True ⇒ the window must be recorded as <c>Blocked</c>. Only an eligibility failure does this:
    /// a window refused for being early or closed is not blocked, it is simply not collectable now, and
    /// marking it Blocked would put a case worker's queue full of people who came on the wrong day.</summary>
    public bool ShouldBlockWindow => Error == ChronicDispenseError.NotEligible;
}

/// <summary>
/// 29.5 — the chronic collection rules at the counter (design 45 §5).
///
/// <para>Three of the four settled decisions meet here:</para>
/// <list type="bullet">
/// <item><b>ONE authorisation for the whole script</b> — this never asks for a per-window authorisation, and
/// nothing creates one. The authorisation is checked once, when the script is written.</item>
/// <item><b>Eligibility RE-VALIDATED at each dispense</b> — "a member whose policy lapses in month 2 is
/// stopped at the pharmacy: the script is BLOCKED, not cancelled, and resumes if eligibility is restored."
/// So an eligibility failure is a WINDOW status, never a script status.</item>
/// <item><b>Limits consumed PER DISPENSE, as collected</b> — the quantity returned here is the one window's
/// remainder, never the script's total. "An uncollected month is never charged."</item>
/// </list>
/// </summary>
public static class ChronicDispensing
{
    /// <summary>
    /// May this window be collected today, and how much?
    /// </summary>
    /// <param name="eligibleNow">Eligibility as re-checked AT THIS MOMENT, not as it stood when the script
    /// was written. That re-check is the whole point of the decision: an authorisation granted in month 1
    /// says nothing about whether the member is still covered in month 2.</param>
    public static ChronicDispenseDecision Evaluate(
        RefillWindow window, DateOnly today, bool eligibleNow, DateOnly? scriptValidUntil)
    {
        ArgumentNullException.ThrowIfNull(window);

        // The script's own validity first. A window inside a script that has expired is not collectable
        // however healthy the window looks — the schedule cannot outlive the prescription it belongs to.
        if (scriptValidUntil is { } until && today > until)
            return new ChronicDispenseDecision(ChronicDispenseError.ScriptExpired, 0m);

        var verdict = RefillWindows.MayDispense(window, today);
        if (!verdict.Allowed)
        {
            return verdict.Refusal switch
            {
                // The refusal NAMES the open date — "a clear refusal naming the open date, not a generic
                // error". The pharmacist has the beneficiary in front of them and must be able to say when
                // to come back.
                WindowRefusal.NotYetOpen =>
                    new ChronicDispenseDecision(ChronicDispenseError.TooEarly, 0m, verdict.OpensAt),
                WindowRefusal.Missed =>
                    new ChronicDispenseDecision(ChronicDispenseError.WindowMissed, 0m),
                WindowRefusal.AlreadyDispensed =>
                    new ChronicDispenseDecision(ChronicDispenseError.WindowComplete, 0m),
                // Already blocked, and still not eligible: it stays blocked.
                _ => new ChronicDispenseDecision(ChronicDispenseError.NotEligible, 0m,
                        BlockReason: "Eligibility could not be confirmed at collection."),
            };
        }

        // ELIGIBILITY IS RE-CHECKED HERE, after the dates and before the hand-over. Checked last of the
        // refusals on purpose: a member who is ineligible AND three weeks early should be told the date, not
        // sent to appeal a coverage decision they will still be too early for.
        if (!eligibleNow)
        {
            return new ChronicDispenseDecision(
                ChronicDispenseError.NotEligible, 0m,
                BlockReason: "Coverage was not active at the time of collection.");
        }

        // PER DISPENSE: this window's remainder, never the script's total. Releasing the whole script would
        // consume three months of a benefit limit for one month of medicine.
        return new ChronicDispenseDecision(ChronicDispenseError.None, verdict.RemainingQuantity);
    }

    /// <summary>A bilingual explanation for the counter. Never a generic error: each refusal has a different
    /// remedy, and a pharmacist who cannot tell them apart cannot advise the person in front of them.</summary>
    public static (string En, string Ar) Explain(ChronicDispenseDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Error switch
        {
            ChronicDispenseError.TooEarly => (
                $"This refill opens on {decision.OpensAt:d MMM yyyy}. It cannot be collected before then.",
                $"يبدأ صرف هذه الجرعة في {decision.OpensAt:d MMM yyyy}، ولا يمكن صرفها قبل ذلك."),
            ChronicDispenseError.WindowMissed => (
                "This refill period has closed and its quantity is forfeited. The next period is unaffected.",
                "انتهت فترة هذا الصرف وسقطت كميتها. الفترة التالية غير متأثرة."),
            ChronicDispenseError.NotEligible => (
                "Coverage is not active, so this refill is blocked. The prescription is NOT cancelled — it "
                + "resumes when coverage is restored.",
                "التغطية غير فعّالة، لذا تم إيقاف هذا الصرف. الوصفة لم تُلغَ — وتُستأنف عند عودة التغطية."),
            ChronicDispenseError.WindowComplete => (
                "This refill has already been collected in full.",
                "تم صرف هذه الجرعة بالكامل."),
            ChronicDispenseError.ScriptExpired => (
                "This prescription's validity has passed.",
                "انتهت صلاحية هذه الوصفة."),
            _ => ("", ""),
        };
    }
}
