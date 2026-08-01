using FluentAssertions;
using Mersal.CallCentre.Domain;

namespace Mersal.CallCentre.Tests;

/// <summary>
/// What is left to test WITHOUT I/O now that caller identity is confirmed off-system.
///
/// <para>The ≥2-identifier-type threshold, the distinct/known-type normalisation and the value-smuggling defence
/// used to live here. They are gone with the challenge that gave them meaning — a threshold on a set no client
/// submits proves nothing, and a test for it would go on passing forever while proving nothing too. The rule
/// that replaced them (a call may only disclose the member it was opened against, and only while open) is I/O
/// by nature and is proved in <see cref="CallControlsTests"/> against a real database.</para>
///
/// <para>What remains here is the one pure invariant worth guarding: the DEFAULT method. It is the reason the
/// rows written before 2026-08 still mean what they meant.</para>
/// </summary>
public class VerificationTests
{
    /// <summary>A verification record defaults to <see cref="VerificationMethod.OnSystem"/>.
    ///
    /// <para>This is not a formality. The table holds two kinds of row that assert different things — "the
    /// platform checked ≥2 identifiers and accepted them" and "an agent says they confirmed the caller by
    /// phone" — and every row written before the column existed is the first kind. Flipping this default (or the
    /// matching DDL default in <c>0006_verification_method.sql</c>) would silently re-label years of audit
    /// evidence as attestations nobody ever made.</para></summary>
    [Fact]
    public void A_verification_record_defaults_to_the_on_system_method()
    {
        new CallerVerification().Method.Should().Be(VerificationMethod.OnSystem);
    }

    /// <summary>An off-system attestation carries no identifier types, and the type list stays empty by default
    /// rather than nullable — a reader must never have to distinguish "no identifiers recorded" from "null".</summary>
    [Fact]
    public void A_verification_record_starts_with_no_identifier_types()
    {
        new CallerVerification().VerifiedIdentifierTypes.Should().BeEmpty();
    }

    /// <summary>The allow-list is retained for reading historical on-system rows; it must still describe them.</summary>
    [Fact]
    public void The_historical_challengeable_types_still_include_phone_the_primary_entry_point()
    {
        VerificationPolicy.ChallengeableTypes.Should().Contain("Phone");
    }

    [Fact]
    public void Call_ref_is_zero_padded_and_prefixed()
    {
        CallRef.Format(2026, 42).Should().Be("CALL-2026-000042");
    }
}
