using FluentAssertions;
using Mersal.Migration.Core;

namespace Mersal.Migration.Tests;

public sealed class DedupeTests
{
    private static NormalizedIdentifier Nid(string v) => IdentifierNormalizer.Normalize(v, IdentifierKind.NationalId);

    private static KnownPerson Person(string id, string idKey, string name, DateOnly dob)
        => new(id, [idKey], name, dob);

    [Fact]
    public void Exact_identifier_hit_auto_merges()
    {
        var known = new[] { Person("p1", "NationalId:29001010123456", "Layla Hassan", new DateOnly(1990, 1, 1)) };
        var candidate = new DedupeCandidate("s1", Nid("29001010123456"), "L Hassan", new DateOnly(1990, 1, 1));

        var outcome = Dedupe.Match(candidate, known);

        outcome.Decision.Should().Be(MatchDecision.AutoMerge);
        outcome.MatchedId.Should().Be("p1");
        outcome.Basis.Should().Be("identifier-exact");
    }

    [Fact]
    public void High_name_score_with_matching_dob_auto_merges()
    {
        var known = new[] { Person("p1", "NationalId:29001010123456", "Mohamed Ali Ibrahim", new DateOnly(1985, 6, 12)) };
        var candidate = new DedupeCandidate("s2", Nid("30001010123456"), "Mohammed Ali Ibrahim", new DateOnly(1985, 6, 12));

        var outcome = Dedupe.Match(candidate, known);

        outcome.Decision.Should().Be(MatchDecision.AutoMerge);
        outcome.Score.Should().BeGreaterThanOrEqualTo(Dedupe.AutoMergeNameScore);
    }

    [Fact]
    public void High_name_score_but_no_dob_agreement_is_review_never_automerge()
    {
        var known = new[] { Person("p1", "NationalId:29001010123456", "Mohamed Ali Ibrahim", new DateOnly(1985, 6, 12)) };
        // Same strong name, but different DOB → must NOT auto-merge; route to review.
        var candidate = new DedupeCandidate("s3", Nid("30001010123456"), "Mohammed Ali Ibrahim", new DateOnly(1991, 2, 2));

        var outcome = Dedupe.Match(candidate, known);

        outcome.Decision.Should().Be(MatchDecision.Review);
        outcome.Basis.Should().Be("name-only-no-dob-agreement");
    }

    [Fact]
    public void Mid_band_score_is_review()
    {
        var known = new[] { Person("p1", "NationalId:29001010123456", "Ahmed Mahmoud", new DateOnly(1980, 3, 3)) };
        var candidate = new DedupeCandidate("s4", Nid("30001010123456"), "Ahmad Mahmod", new DateOnly(1980, 3, 3));

        var outcome = Dedupe.Match(candidate, known);

        outcome.Decision.Should().Be(MatchDecision.Review);
        outcome.Score.Should().BeInRange(Dedupe.ReviewFloor, Dedupe.AutoMergeNameScore);
    }

    [Fact]
    public void Low_score_is_no_match()
    {
        var known = new[] { Person("p1", "NationalId:29001010123456", "Sara Khalil", new DateOnly(1995, 9, 9)) };
        var candidate = new DedupeCandidate("s5", Nid("30001010123456"), "Youssef Nabil", new DateOnly(2000, 1, 1));

        Dedupe.Match(candidate, known).Decision.Should().Be(MatchDecision.NoMatch);
    }

    [Fact]
    public void Empty_known_set_is_no_match()
        => Dedupe.Match(new DedupeCandidate("s", Nid("29001010123456"), "Any Name", null), [])
            .Decision.Should().Be(MatchDecision.NoMatch);
}
