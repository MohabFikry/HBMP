namespace Mersal.Notification.Tests;

/// <summary>Serializes the DB-integration test classes (they open real connections against the shared notification
/// schema + seeded templates). Pure in-memory unit/authz tests stay parallel. The type name avoids the "Collection"
/// suffix (CA1711).</summary>
[Xunit.CollectionDefinition("notification-db", DisableParallelization = true)]
public sealed class NotificationDbTestGroup;
