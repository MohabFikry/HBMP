namespace Mersal.Policy.Tests;

/// <summary>Serializes the DB-integration test classes (they insert into + query the shared policy store).
/// Pure in-memory unit tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("policy-db", DisableParallelization = true)]
public sealed class PolicyDbTestGroup;
