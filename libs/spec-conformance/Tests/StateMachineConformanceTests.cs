using FluentAssertions;
using Mersal.Approvals.Domain;
using Mersal.Claims.Domain;
using Mersal.Emr.Domain;
using Mersal.Orders.Domain;
using Mersal.Patient.Domain;
using Mersal.Pharmacy.Domain;

namespace Mersal.SpecConformance.Tests;

/// <summary>
/// Phase 18.A4 — the guard that keeps <c>HBMP-Design/23-state-machines.md</c> and the code honest about
/// each other.
///
/// For every lifecycle it asserts BOTH directions:
/// <list type="bullet">
/// <item><b>No declared transition missing</b> — everything the document promises is implemented.</item>
/// <item><b>No undeclared transition permitted</b> — the code cannot quietly allow a move the document
/// does not sanction. This is the direction that matters for benefit and privacy safety: an undeclared
/// edge is a lifecycle hole nobody reviewed.</item>
/// <item><b>Every declared state is reachable</b> — a state with no way in is a feature that was designed
/// and then not wired (exactly how report-access UnderReview/Expired/Revoked went missing).</item>
/// </list>
///
/// Where the persisted model deliberately differs from the diagram, the difference is declared here as a
/// named alias or an out-of-scope state WITH ITS REASON, so the mapping is reviewable instead of being a
/// silent exemption.
/// </summary>
public class StateMachineConformanceTests
{
    private static readonly string Spec = StateDiagramParser.LoadSpec();

    /// <summary>One lifecycle under test: how the doc names its states vs how the code models them.</summary>
    private sealed record Machine(
        string Name,
        string Section,
        Func<string, string, bool> CanTransition,
        IReadOnlyList<string> CodeStates,
        IReadOnlyDictionary<string, string> DocToCode,
        IReadOnlyDictionary<string, string> OutOfScope);

    private static IEnumerable<Machine> Machines()
    {
        yield return new Machine(
            "BeneficiaryLifecycle", "## 1. Beneficiary",
            (f, t) => BeneficiaryLifecycle.CanTransition(Parse<BeneficiaryStatus>(f), Parse<BeneficiaryStatus>(t)),
            Names<BeneficiaryStatus>(), NoAlias, NoneOutOfScope);

        yield return new Machine(
            "OrderWorkflow", "## 2. Investigation Order",
            (f, t) => OrderWorkflow.CanTransition(Parse<OrderStatus>(f), Parse<OrderStatus>(t)),
            Names<OrderStatus>(), NoAlias, NoneOutOfScope);

        yield return new Machine(
            "PrescriptionWorkflow", "## 3. Prescription",
            (f, t) => PrescriptionWorkflow.CanTransition(Parse<RxStatus>(f), Parse<RxStatus>(t)),
            Names<RxStatus>(), NoAlias, NoneOutOfScope);

        yield return new Machine(
            "AuthorizationWorkflow", "## 5. Authorization",
            (f, t) => AuthorizationWorkflow.CanTransition(Parse<AuthStatus>(f), Parse<AuthStatus>(t)),
            Names<AuthStatus>(), NoAlias, NoneOutOfScope);

        yield return new Machine(
            "AppointmentWorkflow", "## 6. Appointment",
            (f, t) => AppointmentWorkflow.CanTransition(Parse<AppointmentStatus>(f), Parse<AppointmentStatus>(t)),
            Names<AppointmentStatus>(),
            // The persisted appointment status set is deliberately narrower than the diagram (22 §11):
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Scheduled"] = "Booked" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Requested"] = "pre-booking state; lives on emr.waitlist_entry, not appointment.status",
                ["Waitlisted"] = "pre-booking state; lives on emr.waitlist_entry with its own status column",
                ["Expired"] = "waitlist-window expiry; a property of waitlist_entry, not of an appointment",
                ["InConsultation"] = "queue-ticket state (emr.queue_ticket), not an appointment status",
            });

        yield return new Machine(
            "BatchTransitions", "## 9. Claim Batch",
            (f, t) => BatchTransitions.CanTransition(Parse<BatchStatus>(f), Parse<BatchStatus>(t)),
            Names<BatchStatus>(), NoAlias, NoneOutOfScope);

        yield return new Machine(
            "ReportAccessWorkflow", "## 11. Report Access",
            (f, t) => ReportAccessWorkflow.CanTransition(Parse<ReportAccessStatus>(f), Parse<ReportAccessStatus>(t)),
            Names<ReportAccessStatus>(), NoAlias, NoneOutOfScope);
    }

    public static TheoryData<string> MachineNames()
    {
        var data = new TheoryData<string>();
        foreach (var m in Machines()) data.Add(m.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void Every_declared_transition_is_implemented(string name)
    {
        var m = Machines().Single(x => x.Name == name);
        var missing = InScopeTransitions(m)
            .Where(t => !m.CanTransition(t.From, t.To))
            .Select(t => $"{t.From} --{t.Event}--> {t.To}")
            .ToList();

        missing.Should().BeEmpty(
            "23-state-machines.md {0} declares these transitions but {1} rejects them:\n  {2}",
            m.Section, m.Name, string.Join("\n  ", missing));
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void No_undeclared_transition_is_permitted(string name)
    {
        var m = Machines().Single(x => x.Name == name);
        var declared = InScopeTransitions(m).Select(t => (t.From, t.To)).ToHashSet();

        var undeclared = (
            from source in m.CodeStates
            from target in m.CodeStates
            where source != target && m.CanTransition(source, target) && !declared.Contains((source, target))
            select $"{source} --> {target}").ToList();

        undeclared.Should().BeEmpty(
            "{0} permits transitions that 23-state-machines.md {1} does not declare — an unreviewed " +
            "lifecycle hole. Either implement the spec or amend it:\n  {2}",
            m.Name, m.Section, string.Join("\n  ", undeclared));
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void Every_declared_state_is_reachable(string name)
    {
        var m = Machines().Single(x => x.Name == name);
        var docStates = StateDiagramParser.States(Spec, m.Section)
            .Where(s => !m.OutOfScope.ContainsKey(s))
            .Select(s => Code(m, s))
            .ToList();

        // A state is reachable if something transitions INTO it, or it is a creation state (nothing in the
        // diagram reaches it because it is where the entity is born).
        var reachedByTransition = InScopeTransitions(m).Select(t => t.To).ToHashSet(StringComparer.Ordinal);
        var creationStates = CreationStates(m);

        var orphans = docStates
            .Where(s => !reachedByTransition.Contains(s) && !creationStates.Contains(s))
            .ToList();

        orphans.Should().BeEmpty(
            "{0} declares states with no way in: {1}", m.Name, string.Join(", ", orphans));
    }

    [Theory]
    [MemberData(nameof(MachineNames))]
    public void Out_of_scope_declarations_still_apply(string name)
    {
        // An exemption that outlives its reason quietly widens the gate: if a doc state named here has
        // since been added to the code's own enum, it is in scope after all and the entry must go.
        var m = Machines().Single(x => x.Name == name);
        var docStates = StateDiagramParser.States(Spec, m.Section);

        foreach (var (state, reason) in m.OutOfScope)
        {
            docStates.Should().Contain(state, "out-of-scope entry '{0}' is no longer in the diagram", state);
            m.CodeStates.Should().NotContain(state,
                "'{0}' is exempted as \"{1}\" but {2} now models it — remove the exemption", state, reason, m.Name);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> NoAlias = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> NoneOutOfScope = new(StringComparer.Ordinal);

    /// <summary>
    /// Declared transitions, translated to code state names.
    ///
    /// Out-of-scope states are COLLAPSED THROUGH rather than simply dropped: the appointment diagram routes
    /// <c>CheckedIn → InConsultation → Completed</c>, but <c>InConsultation</c> is a queue-ticket state, so
    /// from the appointment's point of view the declared move really is <c>CheckedIn → Completed</c>.
    /// Dropping the edges instead would make a genuinely declared transition look undeclared.
    /// </summary>
    private static IReadOnlyList<SpecTransition> InScopeTransitions(Machine m)
    {
        var all = StateDiagramParser.Transitions(Spec, m.Section);
        var inScope = all
            .Where(t => !m.OutOfScope.ContainsKey(t.From) && !m.OutOfScope.ContainsKey(t.To))
            .Select(t => t with { From = Code(m, t.From), To = Code(m, t.To) })
            .ToList();

        if (m.OutOfScope.Count == 0) return inScope;

        // Walk each in-scope → out-of-scope edge forward until it lands back in scope.
        foreach (var entry in all.Where(t => !m.OutOfScope.ContainsKey(t.From) && m.OutOfScope.ContainsKey(t.To)))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { entry.To };
            var frontier = new Queue<string>([entry.To]);
            while (frontier.Count > 0)
            {
                var at = frontier.Dequeue();
                foreach (var next in all.Where(t => t.From == at))
                {
                    if (!m.OutOfScope.ContainsKey(next.To))
                        inScope.Add(new SpecTransition(Code(m, entry.From), Code(m, next.To), $"{entry.Event} → {next.Event}"));
                    else if (seen.Add(next.To))
                        frontier.Enqueue(next.To);
                }
            }
        }
        return inScope;
    }

    /// <summary>States the entity can be created in — the targets of <c>[*] --&gt; X</c> edges, plus any
    /// state reachable only from an out-of-scope predecessor (e.g. an appointment is created Booked by
    /// booking or by promotion from the waitlist, both of which live outside this machine).</summary>
    private static IReadOnlySet<string> CreationStates(Machine m)
    {
        var all = StateDiagramParser.Transitions(Spec, m.Section);
        var fromOutOfScope = all.Where(t => m.OutOfScope.ContainsKey(t.From) && !m.OutOfScope.ContainsKey(t.To))
            .Select(t => Code(m, t.To));

        // [*] edges are stripped by the parser, so re-read them from the raw diagram.
        var born = StateDiagramParser.States(Spec, m.Section)
            .Where(s => !m.OutOfScope.ContainsKey(s))
            .Select(s => Code(m, s))
            .Where(s => !all.Any(t => Code(m, t.To) == s))
            .ToList();

        return fromOutOfScope.Concat(born).ToHashSet(StringComparer.Ordinal);
    }

    private static string Code(Machine m, string docState) =>
        m.DocToCode.TryGetValue(docState, out var mapped) ? mapped : docState;

    private static IReadOnlyList<string> Names<TEnum>() where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>();

    private static TEnum Parse<TEnum>(string name) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(name, out var v)
            ? v
            : throw new InvalidOperationException(
                $"23-state-machines.md names state '{name}', which is not a value of {typeof(TEnum).Name}. " +
                "Add an alias or an out-of-scope declaration in StateMachineConformanceTests.");
}
