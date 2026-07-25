namespace Mersal.Finance.Tests;

/// <summary>Serializes the DB-integration test classes (they project into + query the shared finance read-model).
/// Pure in-memory unit/authz tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("finance-db", DisableParallelization = true)]
public sealed class FinanceDbTestGroup;
