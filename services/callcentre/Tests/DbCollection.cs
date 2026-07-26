namespace Mersal.CallCentre.Tests;

/// <summary>Serializes the DB-integration test classes (they read/write the shared callcentre schema). Pure
/// in-memory unit/authz tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("callcentre-db", DisableParallelization = true)]
public sealed class CallCentreDbTestGroup;
