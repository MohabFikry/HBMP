namespace Mersal.Admin.Api;

/// <summary>
/// 31.6 — response shapes this service returns that had no name.
///
/// <para>An anonymous object returned from an endpoint IS a contract; it was simply unwritten, so the OpenAPI
/// drift gate compared the route and the request and passed over the body. Every record here carries exactly
/// the property names its anonymous object carried, in the same casing, so the JSON is byte-identical — what
/// changes is that the shape appears in <c>docs/api/admin.json</c>.</para>
/// </summary>

// ---------------------------------------------------------------------------------- branch and payer scope

/// <summary>
/// Which branches a user may act in, and which of them is home.
/// </summary>
/// <param name="HomeBranch">
/// NULL when no home branch resolved. Absent is not the same as "the first permitted one": a user with two
/// permitted branches and no home has to be ASKED, and defaulting here would silently pick one for them.
/// </param>
public sealed record BranchScopeView(Guid? HomeBranch, IReadOnlyList<Guid> PermittedBranches);

/// <summary>The branch a user just switched into, with the set it was chosen from.</summary>
/// <param name="ActiveBranch">
/// Carried twice, as <c>activeBranch</c> and <c>activeBranchId</c>. Both names are in the published contract
/// and clients read one or the other; dropping either here would be a silent breaking change.
/// </param>
public sealed record ActiveBranchView(
    Guid? ActiveBranch, Guid? ActiveBranchId, IReadOnlyList<Guid> PermittedBranches);

/// <summary>
/// Which payers a user's reporting is scoped to.
/// </summary>
/// <param name="Unrestricted">
/// True when no assignment narrows them — stated as its own field rather than implied by an empty list,
/// because "every payer" and "no payer" are opposite answers that would otherwise look identical.
/// </param>
public sealed record PayerScopeView(bool Unrestricted, IReadOnlyList<Guid> PayerIds);

// ---------------------------------------------------------------------------------- governance

/// <summary>One master-data code as it stood at a point in time.</summary>
public sealed record MasterDataVersionView(
    Guid VersionId, int VersionNo, string? AttributesJson,
    DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo);

/// <summary>A system-configuration entry after a write, with the version it now carries.</summary>
/// <param name="VersionNo">Returned so the caller can write again without re-reading — optimistic concurrency
/// needs the number in the hand of whoever is about to use it.</param>
public sealed record SystemConfigView(Guid ConfigId, string Key, string? Value, string Type, int VersionNo);

/// <summary>A tenant's identity and whether it is live.</summary>
public sealed record TenantView(string TenantId, string Name, bool Active);

/// <summary>A feature flag's new state for one tenant.</summary>
public sealed record FeatureFlagView(string? Tenant, string Feature, bool Enabled);

/// <summary>
/// A programme limit's new cap, and whether the tenant is already past it.
/// </summary>
/// <param name="AlreadyOverCap">
/// Setting a cap below current usage is permitted and REPORTED. Refusing it would leave an administrator
/// unable to tighten a limit that is already being exceeded, which is exactly when they most want to.
/// </param>
/// <remarks>Named for the WRITE — `ProgramLimitView` already describes a limit as it is read.</remarks>
public sealed record ProgramLimitChangeView(
    string? Tenant, string Limit, long MaxValue, long? CurrentUsage, bool AlreadyOverCap);

// ---------------------------------------------------------------------------------- policy writes

/// <summary>A validity policy after a change, and who it applies to.</summary>
/// <param name="AppliesTo">
/// Spelled out because it is the question an administrator actually has: changing a policy never reaches
/// backwards. Anything already issued keeps the expiry it was issued with.
/// </param>
public sealed record ValidityPolicyChangeView(
    string Artefact, int Days, string AppliesTo, int Version, DateTimeOffset EffectiveFrom);

/// <summary>A document-validity policy after a change. See <see cref="ValidityPolicyChangeView"/>.</summary>
/// <param name="Days">Null when this write changed only the warning cadence — a change is not both.</param>
/// <param name="WarnDays">The reminder cadence, in days before expiry. Null when unchanged by this write.</param>
public sealed record DocumentValidityChangeView(
    string Kind, int? Days, IReadOnlyList<int>? WarnDays, string AppliesTo, int? Version);

/// <summary>A session policy's identity after a write.</summary>
public sealed record SessionPolicyView(Guid PolicyId, string Tier, DateTimeOffset EffectiveFrom);

/// <summary>A device policy's identity after a write.</summary>
public sealed record DevicePolicyView(Guid PolicyId, string? Role, DateTimeOffset EffectiveFrom);

// ---------------------------------------------------------------------------------- break-glass + review

/// <summary>A break-glass grant's state after an act on it.</summary>
public sealed record GrantStatusView(Guid GrantId, string Status);

/// <summary>An activated grant, with when it lapses. The expiry is the point of activating one.</summary>
public sealed record GrantActivationView(Guid GrantId, string Status, DateTimeOffset? ExpiresAt);

/// <summary>Whether this grant admits the caller to what they asked for.</summary>
public sealed record GrantAccessView(Guid GrantId, bool Granted);

/// <summary>What an access-review sweep expired, so the campaign's effect is visible without a re-read.</summary>
public sealed record AccessReviewSweepView(Guid CampaignId, int AutoExpired);

/// <summary>The roles a subject actually holds, resolved rather than as assigned.</summary>
public sealed record EffectiveRolesView(string Subject, string? Tenant, IReadOnlyList<string> Roles);

/// <summary>One pair of roles that must not be held together, and why.</summary>
public sealed record SodConflictView(string TokenA, string TokenB, string Reason);

/// <summary>A master-data code currently in force — the governance read surface.</summary>
/// <param name="Retired">
/// In force and retired are different things: a retired code still governs the rows already written against
/// it, and hiding it here would make those rows unexplainable.
/// </param>
public sealed record MasterDataInForceView(
    Guid VersionId, string System, string Code, int VersionNo, bool Retired,
    DateTimeOffset EffectiveFrom, string? Rationale);

/// <summary>A newly-published master-data version.</summary>
public sealed record MasterDataCreatedView(
    Guid VersionId, string System, string Code, int VersionNo, DateTimeOffset EffectiveFrom);

/// <summary>A system-config entry currently in force, scoped to its tenant.</summary>
public sealed record SystemConfigInForceView(
    Guid ConfigId, string? TenantId, string Key, string Type, string? Value, int VersionNo,
    DateTimeOffset EffectiveFrom);

/// <summary>
/// One access-review campaign and where its items stand.
/// </summary>
/// <remarks>
/// The counts are broken out rather than summarised because they answer different questions: `pending` is
/// work outstanding, `revoked` is access actually removed, and `autoExpired` is access removed BY THE
/// DEADLINE PASSING rather than by anyone deciding. A single "closed" figure would fold the last two
/// together, and only one of them means somebody reviewed anything.
/// </remarks>
public sealed record AccessReviewCampaignView(
    Guid CampaignId, string Name, string Status, DateTimeOffset? DueAt,
    int Total, int Pending, int Recertified, int Revoked, int AutoExpired);

