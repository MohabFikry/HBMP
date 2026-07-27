using Mersal.Provider.Domain;

namespace Mersal.Provider.Infrastructure;

/// <summary>
/// Phase 19.1b — has any claim already been adjudicated against this tier assignment?
///
/// The guard on CORRECTING an assignment. Correcting retroactively voids a tier statement, which is the right
/// repair for a mis-assignment nobody has acted on. Once a claim has been priced against it, money has moved on
/// the strength of that tier, and rewriting it would leave settled claims referencing a tier the record no
/// longer admits to. From there the fix is a claims adjustment, not a tier edit.
///
/// A seam rather than a direct query because claims lives in another service and provider-service must not read
/// its schema. The wiring is deferred like <c>IFulfillmentResolver</c> in claims (phase 10b).
/// </summary>
public interface IAdjudicatedClaimProbe
{
    Task<int> CountAdjudicatedAgainstAsync(ProviderNetworkAssignment assignment, CancellationToken ct = default);
}

/// <summary>
/// Default until the claims read-model query is wired: reports zero.
///
/// This is a KNOWN OPEN GAP, not a safe default. It means a correction succeeds today even where a claim has
/// been adjudicated against the assignment — the reverse of the intended fail-closed posture. It is registered
/// this way deliberately rather than blocking every correction, because refusing all of them would leave the
/// mis-assignment case with no repair at all, which is the problem the third verb exists to solve. The audit
/// event and the ProviderTierCorrected outbox event both fire regardless, so a correction is never silent and
/// is reconcilable after the fact. Tracked for the claims read-model wiring.
/// </summary>
public sealed class UnwiredAdjudicatedClaimProbe : IAdjudicatedClaimProbe
{
    public Task<int> CountAdjudicatedAgainstAsync(ProviderNetworkAssignment assignment, CancellationToken ct = default) =>
        Task.FromResult(0);
}
