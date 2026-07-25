namespace Mersal.Claims.Tests;

/// <summary>Serializes the DB-integration test classes (they insert into + query the shared claims store).
/// Pure in-memory unit/authz tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("claims-db", DisableParallelization = true)]
public sealed class ClaimsDbTestGroup;
