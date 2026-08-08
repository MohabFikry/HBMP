using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Events.Tests;

/// <summary>
/// A source scan proving every event mirrored to emr's care-episode feed carries <c>encounterId</c> (ADR-0031).
///
/// <para>This is the field the whole design turns on, and its absence is silent in the worst way. A payload
/// that drops it does not fail, does not warn and does not dead-letter — <c>CareEpisodeMapping</c> correctly
/// declines to attach a step it cannot place, the consumer acks, and the only symptom is a timeline that is
/// quietly missing the order. That is precisely the state this whole slice existed to end: both
/// <c>orders.order</c> and <c>pharmacy.prescription</c> have HELD the column since phase 4 and simply never
/// put it on the wire, and nothing anywhere said so.</para>
/// </summary>
public class CareFeedEnvelopeArchitectureTests
{
    /// <summary>
    /// The mirrored events whose type is a computed expression at the publish site, so a scan that matches on
    /// the literal name cannot see them.
    ///
    /// <para>All five approvals decisions are enqueued from ONE call in <c>Decisions.cs</c> as
    /// <c>EnqueueAsync(eventType, "approvals.events", …)</c>, where <c>eventType</c> comes from
    /// <c>EventType(decision)</c>. Listing them here rather than teaching the regex to chase a local variable
    /// keeps the hole VISIBLE: the set is asserted below, so a newly added literal-named event cannot slip
    /// into it and escape the scan by accident.</para>
    /// </summary>
    private static readonly HashSet<string> PublishedUnderAComputedName = new(StringComparer.Ordinal)
    {
        "AuthApproved", "AuthPartiallyApproved", "AuthRejected", "AuthOverridden", "AuthEmergencyApproved",
    };

    [Fact]
    public void Every_mirrored_event_published_by_name_carries_its_encounter()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var source = File.ReadAllText(file);
            foreach (var eventType in CareFeed.EventTypes)
            {
                foreach (Match publish in PublishSites(source, eventType))
                {
                    var payload = PayloadOf(source, publish.Index + publish.Length);
                    if (payload.Contains("encounterId", StringComparison.Ordinal)) continue;

                    var line = source[..publish.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Relative(file)}:{line} publishes {eventType} without encounterId — it is " +
                                  "mirrored to emr's care-episode feed, which will silently drop the step");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a step cannot be attached to an episode the event does not name:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_events_this_scan_cannot_see_are_exactly_the_ones_declared_unseeable()
    {
        // Guards the guard, in the direction that actually bites: a mirrored event with NO literal publish
        // site is one this scan silently skips, and "the test passed" would then mean "the test looked at
        // nothing". Set equality — not a count — so adding an event without a publisher fails here, and
        // giving one of the five a literal name (without removing it from the list above) fails here too.
        var unseen = CareFeed.EventTypes
            .Where(t => SourceFiles().All(f => PublishSites(File.ReadAllText(f), t).Count == 0))
            .ToHashSet(StringComparer.Ordinal);

        unseen.Should().BeEquivalentTo(PublishedUnderAComputedName,
            "every mirrored event must either be scannable by name or be a declared, explained exception");
    }

    private static MatchCollection PublishSites(string source, string eventType) =>
        Regex.Matches(source, $@"EnqueueAsync\(\s*""{Regex.Escape(eventType)}""\s*,\s*""[^""]+""\s*,");

    /// <summary>The anonymous-object payload that follows the queue argument, read to its balanced close so a
    /// nested <c>new { … }</c> is included rather than truncating the search early.</summary>
    private static string PayloadOf(string source, int start)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return source[start..(i + 1)];
                    break;
                case ')' when depth == 0: return source[start..i];
            }
        }
        return source[start..];
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "services"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
