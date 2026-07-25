namespace Mersal.Approvals.Tests;

/// <summary>Serializes the DB-integration test classes (they open real connections against the shared auth
/// sequence and the append-only ledger). Pure in-memory unit/authz tests stay parallel. The type name avoids the
/// "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("approvals-db", DisableParallelization = true)]
public sealed class ApprovalsDbTestGroup;
