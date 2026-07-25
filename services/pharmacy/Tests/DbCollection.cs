namespace Mersal.Pharmacy.Tests;

/// <summary>Serializes the DB-integration test classes: the atomic-dispense concurrency test opens many parallel
/// connections and races on the shared prescription sequence, so it must not run alongside the other datastore tests
/// (xUnit parallelizes across classes by default). Pure in-memory unit/authz tests stay parallel.</summary>
[Xunit.CollectionDefinition("pharmacy-db", DisableParallelization = true)]
public sealed class PharmacyDbTestGroup;
