namespace Mersal.Inventory.Tests;

/// <summary>
/// One collection for every DB-backed inventory suite, which DISABLES PARALLELISM BETWEEN THEM.
///
/// <para>Not a performance choice — a correctness one, and it was earned. The suites share one test tenant and
/// each cleans up by deleting that tenant's rows, so with xUnit's default cross-class parallelism one class's
/// teardown wiped another's fixture mid-test. The symptom was two unrelated tests failing intermittently with
/// impossible balances, which reads exactly like a concurrency bug in the code under test — the most expensive
/// kind of false signal a ledger suite can produce.</para>
///
/// <para>Tests WITHIN a class already run sequentially, so this closes the remaining gap. The genuine
/// concurrency proof (two parallel issues of the last unit) runs its own tasks inside one test and is
/// unaffected.</para>
/// </summary>
[CollectionDefinition("inventory-db", DisableParallelization = true)]
public sealed class InventoryDbSuites;

/// <summary>
/// A clock pinned to a stated instant, so expiry and threshold boundaries are asserted rather than raced.
///
/// TOP-LEVEL, not nested inside a test class, and that is not a style preference: <c>check-invariant-registry.py</c>
/// attributes a test method to the nearest class declaration ABOVE it, so a nested helper declared before the
/// tests silently re-parents every one of them. The registry then reports the invariant's tests as missing —
/// which is the guard working, and the fix belongs here rather than in the guard.
/// </summary>
internal sealed class FixedClock(DateTimeOffset at) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => at;
}
