using System.Text.RegularExpressions;

namespace Mersal.SpecConformance.Tests;

/// <summary>One declared edge of a spec state machine.</summary>
public sealed record SpecTransition(string From, string To, string Event);

/// <summary>
/// Reads the <c>stateDiagram-v2</c> blocks out of <c>HBMP-Design/23-state-machines.md</c>. The document is
/// the contract; this makes it executable, so a diagram edited without the code (or code edited without
/// the diagram) fails the build rather than drifting silently.
///
/// Mermaid lines look like <c>Requested --&gt; PendingApproval: gated service</c>. <c>[*]</c> is the
/// pseudo start/end node: an edge from it is creation (not a transition between states) and an edge to it
/// marks a terminal state — both are recorded but excluded from the transition set.
/// </summary>
public static class StateDiagramParser
{
    private static readonly Regex Edge = new(
        @"^\s*(?<from>\[\*\]|\w+)\s*-->\s*(?<to>\[\*\]|\w+)\s*(?::\s*(?<event>.*))?$",
        RegexOptions.Compiled);

    /// <summary>Parse the diagram under the heading that starts with <paramref name="sectionPrefix"/>
    /// (e.g. "## 2. Investigation Order").</summary>
    public static IReadOnlyList<SpecTransition> Transitions(string markdown, string sectionPrefix)
    {
        var body = Section(markdown, sectionPrefix);
        var block = DiagramBlock(body, sectionPrefix);
        var result = new List<SpecTransition>();
        foreach (var line in block.Split('\n'))
        {
            var m = Edge.Match(line);
            if (!m.Success) continue;
            var from = m.Groups["from"].Value;
            var to = m.Groups["to"].Value;
            if (from == "[*]" || to == "[*]") continue;   // creation / termination markers, not transitions
            result.Add(new SpecTransition(from, to, m.Groups["event"].Value.Trim()));
        }
        if (result.Count == 0)
            throw new InvalidOperationException($"no transitions parsed for '{sectionPrefix}' — the diagram format changed");
        return result;
    }

    /// <summary>Every state named anywhere in the section's diagram, including terminal-only ones.</summary>
    public static IReadOnlySet<string> States(string markdown, string sectionPrefix)
    {
        var block = DiagramBlock(Section(markdown, sectionPrefix), sectionPrefix);
        var states = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in block.Split('\n'))
        {
            var m = Edge.Match(line);
            if (!m.Success) continue;
            foreach (var g in new[] { "from", "to" })
                if (m.Groups[g].Value is var v && v != "[*]") states.Add(v);
        }
        return states;
    }

    private static string Section(string markdown, string sectionPrefix)
    {
        var start = markdown.IndexOf(sectionPrefix, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"section '{sectionPrefix}' not found in 23-state-machines.md");
        var next = markdown.IndexOf("\n## ", start + sectionPrefix.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    private static string DiagramBlock(string section, string sectionPrefix)
    {
        var open = section.IndexOf("stateDiagram-v2", StringComparison.Ordinal);
        if (open < 0) throw new InvalidOperationException($"no stateDiagram-v2 block under '{sectionPrefix}'");
        var close = section.IndexOf("```", open, StringComparison.Ordinal);
        return close < 0 ? section[open..] : section[open..close];
    }

    /// <summary>Load the spec document from the repository root.</summary>
    public static string LoadSpec()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("repository root (HbmpPlatform.sln) not found");
        return File.ReadAllText(Path.Combine(root, "HBMP-Design", "23-state-machines.md"));
    }
}
