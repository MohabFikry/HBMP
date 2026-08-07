namespace Mersal.Prescribing;

/// <summary>
/// A refill window's stored status (design 45 §5).
///
/// <para><see cref="Open"/> is never written. Dispensability is computed from the dates
/// (<see cref="RefillWindows.MayDispense"/>), and the value exists only so a row can be read back the way the
/// design's state list describes it. See the design note for why: a sweeper-written Open would make a
/// background-job outage refuse patients at the counter.</para>
/// </summary>
public enum WindowStatus { Pending, Open, Dispensed, PartiallyDispensed, Missed, Blocked }

/// <summary>Why a window may not be dispensed. <see cref="None"/> means it may.</summary>
public enum WindowRefusal
{
    None,
    /// <summary>Before <c>opens_at</c> — the scheduled date minus the early tolerance.</summary>
    NotYetOpen,
    /// <summary>Past <c>closes_at</c>, or already forfeited. The quantity cannot be claimed later.</summary>
    Missed,
    /// <summary>Eligibility failed at the counter. NOT the patient's doing, and NOT a cancellation — the
    /// window resumes if eligibility is restored while it is still inside its dates.</summary>
    Blocked,
    /// <summary>Everything allocated to this window has already been handed over.</summary>
    AlreadyDispensed,
}

/// <summary>One window of a chronic script.</summary>
/// <param name="OpensAt">The scheduled open date MINUS the early tolerance, stored rather than computed:
/// the tolerance is configurable, and a window issued under a 5-day tolerance keeps it if the setting
/// changes.</param>
public sealed record RefillWindow(
    int WindowNo,
    DateOnly ScheduledOpen,
    DateOnly OpensAt,
    DateOnly ClosesAt,
    decimal AllocatedQuantity,
    decimal DispensedQuantity,
    WindowStatus Status)
{
    public decimal RemainingQuantity => Math.Max(0m, AllocatedQuantity - DispensedQuantity);
}

/// <summary>The counter's answer, with everything a pharmacist needs to explain it.</summary>
public sealed record DispenseVerdict(bool Allowed, WindowRefusal Refusal, DateOnly OpensAt, decimal RemainingQuantity);

/// <summary>
/// 29.5 — the refill-window lifecycle (design 45 §5).
///
/// <para><b>The counter enforces; the sweeper records.</b> Dispensability is a pure function of the dates and
/// the quantities, so:</para>
/// <list type="bullet">
/// <item>A stalled sweeper <b>delays a forfeiture</b>, and cannot prevent a collection — because nothing has
/// to promote a window to Open before it can be used.</item>
/// <item>A stalled sweeper <b>cannot let a forfeited window be collected</b> either — because
/// <c>closes_at</c> is in this predicate rather than in the sweeper.</item>
/// </list>
/// <para>What the sweeper is genuinely authoritative about is the RECORD: a forfeiture is money that will
/// never be claimed, and it needs a timestamp and an actor. That is all it writes.</para>
/// </summary>
public static class RefillWindows
{
    /// <summary>Design 45 §5's default early tolerance. Configurable; this is the value a tenant that has not
    /// set one gets.</summary>
    public const int DefaultEarlyToleranceDays = 5;

    /// <summary>May this window be dispensed today?</summary>
    public static DispenseVerdict MayDispense(RefillWindow window, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Terminal states first, so a Missed window past its close reports Missed rather than NotYetOpen.
        if (window.Status == WindowStatus.Missed) return Refuse(window, WindowRefusal.Missed);
        if (window.Status == WindowStatus.Blocked) return Refuse(window, WindowRefusal.Blocked);
        if (window.RemainingQuantity <= 0) return Refuse(window, WindowRefusal.AlreadyDispensed);

        // THE dates, not the status. A window still marked Pending because nothing has swept it is dispensable
        // if today is inside its window, and refused if today is past it.
        if (on < window.OpensAt) return Refuse(window, WindowRefusal.NotYetOpen);
        if (on > window.ClosesAt) return Refuse(window, WindowRefusal.Missed);

        return new DispenseVerdict(true, WindowRefusal.None, window.OpensAt, window.RemainingQuantity);
    }

    /// <summary>
    /// Should the sweeper forfeit this window?
    /// </summary>
    /// <remarks>
    /// <para>Only a window that closed with NOTHING collected. Three exclusions, each for its own reason:</para>
    /// <list type="bullet">
    /// <item><b>Already Missed</b> — so a second pass matches nothing and cannot rewrite a <c>missed_at</c>
    /// an investigation may already rely on. This is what makes forfeiting idempotent.</item>
    /// <item><b>Blocked</b> — the platform refused the patient; sweeping it to Missed would relabel the
    /// system's own refusal as the beneficiary's no-show and destroy the only signal the case team has.</item>
    /// <item><b>Partially dispensed</b> — the beneficiary DID attend. Design 45 §5 forfeits the window that
    /// closes undispensed; marking a partial as Missed would misreport someone who came.</item>
    /// </list>
    /// </remarks>
    public static bool ShouldForfeit(RefillWindow window, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(window);

        return window.Status is WindowStatus.Pending or WindowStatus.Open
               && window.DispensedQuantity == 0
               && on > window.ClosesAt;
    }

    /// <summary>Record the forfeiture. The quantity is NOT zeroed — what was allocated stays visible, because
    /// "how much was forfeited" is the question a benefit reconciliation asks.</summary>
    public static RefillWindow Forfeit(RefillWindow window, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(window);
        _ = at;   // the caller stamps missed_at on the row; the domain records only the state change
        return window with { Status = WindowStatus.Missed };
    }

    /// <summary>Eligibility failed at the counter. The script is NOT cancelled.</summary>
    public static RefillWindow Block(RefillWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window with { Status = WindowStatus.Blocked };
    }

    /// <summary>Eligibility restored. Back to Pending — and the DATES decide whether that is any use: a window
    /// unblocked after it closed is still refused, because being blocked does not extend a script.</summary>
    public static RefillWindow Unblock(RefillWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Status == WindowStatus.Blocked ? window with { Status = WindowStatus.Pending } : window;
    }

    /// <summary>Apply a collection, moving the window to Dispensed or PartiallyDispensed.</summary>
    public static RefillWindow Dispense(RefillWindow window, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var dispensed = Math.Min(window.AllocatedQuantity, window.DispensedQuantity + quantity);
        return window with
        {
            DispensedQuantity = dispensed,
            Status = dispensed >= window.AllocatedQuantity
                ? WindowStatus.Dispensed
                : WindowStatus.PartiallyDispensed,
        };
    }

    private static DispenseVerdict Refuse(RefillWindow w, WindowRefusal why) =>
        new(false, why, w.OpensAt, w.RemainingQuantity);
}
