namespace Mersal.Reporting.Tests;

/// <summary>Serializes the DB-integration test classes (they project into + query the shared reporting read-model).
/// Pure in-memory unit/authz tests stay parallel. The type name avoids the "Collection" suffix (CA1711).</summary>
[Xunit.CollectionDefinition("reporting-db", DisableParallelization = true)]
public sealed class ReportingDbTestGroup;
