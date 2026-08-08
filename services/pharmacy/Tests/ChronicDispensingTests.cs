using FluentAssertions;
using Mersal.Pharmacy.Domain;
using Mersal.Prescribing;

namespace Mersal.Pharmacy.Tests;

/// <summary>
/// 29.5 / design 45 §5 — the chronic collection rules at the counter.
///
/// <para>Three settled decisions meet here, and the tests are named for the beneficiary each one protects:
/// one authorisation for the whole script, eligibility re-validated at each dispense, and limits consumed
/// per dispense as collected.</para>
/// </summary>
public class ChronicDispensingTests
{
    private static readonly DateOnly Scheduled = new(2026, 5, 1);
    private static readonly DateOnly ScriptEnd = new(2026, 6, 29);

    private static RefillWindow Window(
        WindowStatus status = WindowStatus.Pending, decimal allocated = 30m, decimal dispensed = 0m) =>
        new(2, Scheduled, Scheduled.AddDays(-5), Scheduled.AddDays(29), allocated, dispensed, status);

    [Fact]
    public void A_lapsed_member_is_BLOCKED_at_the_counter_and_the_script_is_not_cancelled()
    {
        // Design 45 §5's headline case: "A member whose policy lapses in month 2 is stopped at the pharmacy —
        // the script is BLOCKED, not cancelled, and resumes if eligibility is restored."
        var decision = ChronicDispensing.Evaluate(Window(), Scheduled, eligibleNow: false, ScriptEnd);

        decision.Allowed.Should().BeFalse();
        decision.Error.Should().Be(ChronicDispenseError.NotEligible);
        decision.ShouldBlockWindow.Should().BeTrue();

        var (en, _) = ChronicDispensing.Explain(decision);
        en.Should().Contain("NOT cancelled", "the pharmacist must be able to say the script survives this");
    }

    [Fact]
    public void A_blocked_window_resumes_when_eligibility_is_restored()
    {
        var blocked = RefillWindows.Block(Window());

        // Still not eligible: still refused.
        ChronicDispensing.Evaluate(blocked, Scheduled, eligibleNow: false, ScriptEnd)
            .Allowed.Should().BeFalse();

        // Restored, and still inside the window's dates: collectable again. Nothing was cancelled.
        var restored = RefillWindows.Unblock(blocked);
        ChronicDispensing.Evaluate(restored, Scheduled, eligibleNow: true, ScriptEnd)
            .Allowed.Should().BeTrue();
    }

    [Fact]
    public void Eligibility_is_re_checked_at_the_counter_not_taken_from_the_authorisation()
    {
        // ONE authorisation for the whole script — so the authorisation cannot be what makes this decision.
        // The SAME window, the SAME day, two different answers, decided only by the eligibility re-check.
        ChronicDispensing.Evaluate(Window(), Scheduled, eligibleNow: true, ScriptEnd).Allowed.Should().BeTrue();
        ChronicDispensing.Evaluate(Window(), Scheduled, eligibleNow: false, ScriptEnd).Allowed.Should().BeFalse();
    }

    [Fact]
    public void An_early_collection_is_refused_with_the_open_date_named()
    {
        var decision = ChronicDispensing.Evaluate(Window(), Scheduled.AddDays(-10), eligibleNow: true, ScriptEnd);

        decision.Error.Should().Be(ChronicDispenseError.TooEarly);
        decision.OpensAt.Should().Be(Scheduled.AddDays(-5));
        // "A clear refusal NAMING the open date, not a generic error."
        ChronicDispensing.Explain(decision).En.Should().Contain("26 Apr 2026");
    }

    [Fact]
    public void An_early_AND_ineligible_collection_is_told_the_date_rather_than_sent_to_appeal()
    {
        // Order of refusals matters to the person at the counter: someone three weeks early should be told
        // when to come back, not sent to contest a coverage decision they will still be too early for.
        var decision = ChronicDispensing.Evaluate(Window(), Scheduled.AddDays(-10), eligibleNow: false, ScriptEnd);

        decision.Error.Should().Be(ChronicDispenseError.TooEarly);
        decision.ShouldBlockWindow.Should().BeFalse("being early is not an eligibility failure");
    }

    [Fact]
    public void Only_the_windows_remainder_is_dispensable_never_the_scripts_total()
    {
        // "Limits are consumed PER DISPENSE, as collected." Releasing the script's whole total would consume
        // three months of a benefit limit for one month of medicine.
        var partial = Window(status: WindowStatus.PartiallyDispensed, allocated: 30m, dispensed: 10m);

        ChronicDispensing.Evaluate(partial, Scheduled, eligibleNow: true, ScriptEnd)
            .DispensableQuantity.Should().Be(20m);
    }

    [Fact]
    public void A_missed_window_cannot_be_collected_and_does_not_block_the_next_one()
    {
        var missed = Window(status: WindowStatus.Missed);

        var decision = ChronicDispensing.Evaluate(missed, Scheduled.AddDays(40), eligibleNow: true, ScriptEnd);

        decision.Error.Should().Be(ChronicDispenseError.WindowMissed);
        decision.ShouldBlockWindow.Should().BeFalse();
        ChronicDispensing.Explain(decision).En.Should().Contain("next period is unaffected");
    }

    [Fact]
    public void A_window_inside_an_expired_script_is_refused_however_healthy_the_window_looks()
    {
        // The schedule cannot outlive the prescription it belongs to.
        var decision = ChronicDispensing.Evaluate(
            Window(), today: ScriptEnd.AddDays(1), eligibleNow: true, scriptValidUntil: ScriptEnd);

        decision.Error.Should().Be(ChronicDispenseError.ScriptExpired);
    }

    [Fact]
    public void Every_refusal_is_bilingual_and_specific()
    {
        // Each refusal has a DIFFERENT remedy — come back later, appeal coverage, ask for a new script — and a
        // pharmacist who cannot tell them apart cannot advise the person in front of them.
        foreach (var error in new[]
                 {
                     ChronicDispenseError.TooEarly, ChronicDispenseError.WindowMissed,
                     ChronicDispenseError.NotEligible, ChronicDispenseError.WindowComplete,
                     ChronicDispenseError.ScriptExpired,
                 })
        {
            var (en, ar) = ChronicDispensing.Explain(new ChronicDispenseDecision(error, 0m, Scheduled));
            en.Should().NotBeNullOrWhiteSpace($"{error} needs an explanation");
            ar.Should().NotBeNullOrWhiteSpace($"{error} needs an Arabic explanation");
        }
    }
}
