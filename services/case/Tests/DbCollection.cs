namespace Mersal.Case.Tests;

/// <summary>Serializes the DB-integration test classes (they read/write the shared case schema). Pure in-memory
/// unit/authz tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("case-db", DisableParallelization = true)]
public sealed class CaseDbTestGroup;
