namespace Mersal.Orders.Tests;

/// <summary>Serializes the DB-integration test classes: the atomic-consume concurrency test opens many parallel
/// connections and races on the shared order sequence, so it must not run alongside the other datastore tests
/// (xUnit parallelizes across classes by default). Pure in-memory unit/authz tests stay parallel.</summary>
[Xunit.CollectionDefinition("orders-db", DisableParallelization = true)]
public sealed class OrdersDbTestGroup;
