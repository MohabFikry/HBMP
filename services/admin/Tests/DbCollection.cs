namespace Mersal.Admin.Tests;

/// <summary>Serializes the DB-integration test classes (they open real connections against the admin schema). Pure
/// unit/authz tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("admin-db", DisableParallelization = true)]
public sealed class AdminDbTestGroup;
