namespace Mersal.Provider.Domain;

/// <summary>Outcome of a guarded onboarding transition: allowed, or denied with an actionable reason.</summary>
public readonly record struct TransitionResult(bool Allowed, string? Reason)
{
    public static readonly TransitionResult Ok = new(true, null);
    public static TransitionResult Blocked(string reason) => new(false, reason);
}

/// <summary>The explicit, auditable provider onboarding state machine (phase 2b.2, FR-NET-003/004):
/// Draft → DocumentsCollected → Credentialed → Contracted → Activated, with Suspended/Terminated as
/// post-activation states. Each forward step requires the prior work complete; the class is pure so the
/// guards are unit-tested without a database.</summary>
public static class OnboardingWorkflow
{
    /// <summary>Snapshot of the facts the guards evaluate, so the state machine stays free of EF types.</summary>
    public readonly record struct Readiness(
        bool HasPrimaryLocation,
        bool HasMandatoryCredentials,
        bool MandatoryCredentialsValid,
        bool HasActiveContract);

    /// <summary>Can the provider advance from <paramref name="from"/> to <paramref name="to"/> given
    /// current <paramref name="readiness"/>? Only the legal forward/again transitions are modelled;
    /// anything else is denied (default-deny).</summary>
    public static TransitionResult CanTransition(OnboardingState from, OnboardingState to, Readiness readiness) => (from, to) switch
    {
        (OnboardingState.Draft, OnboardingState.DocumentsCollected) =>
            readiness.HasPrimaryLocation ? TransitionResult.Ok
                : TransitionResult.Blocked("A primary location must be set before documents are collected."),

        (OnboardingState.DocumentsCollected, OnboardingState.Credentialed) =>
            readiness.HasMandatoryCredentials ? TransitionResult.Ok
                : TransitionResult.Blocked("All mandatory credential documents must be attached."),

        (OnboardingState.Credentialed, OnboardingState.Contracted) =>
            readiness.HasActiveContract ? TransitionResult.Ok
                : TransitionResult.Blocked("An active contract with service lines is required."),

        (OnboardingState.Contracted, OnboardingState.Activated) =>
            GuardActivation(readiness),

        // Suspend/Terminate are reachable from any post-Draft operational state.
        (_, OnboardingState.Suspended) when from is not OnboardingState.Draft and not OnboardingState.Terminated =>
            TransitionResult.Ok,
        (_, OnboardingState.Terminated) when from is not OnboardingState.Terminated =>
            TransitionResult.Ok,

        // Reactivation after suspension returns to Activated only if still fully satisfied.
        (OnboardingState.Suspended, OnboardingState.Activated) => GuardActivation(readiness),

        _ => TransitionResult.Blocked($"Illegal onboarding transition {from} → {to}."),
    };

    /// <summary>Activation requires a primary location, valid mandatory credentials, and an active contract.</summary>
    public static TransitionResult GuardActivation(Readiness r)
    {
        if (!r.HasActiveContract) return TransitionResult.Blocked("Cannot activate: no active contract.");
        if (!r.HasMandatoryCredentials) return TransitionResult.Blocked("Cannot activate: mandatory credentials missing.");
        if (!r.MandatoryCredentialsValid) return TransitionResult.Blocked("Cannot activate: a mandatory credential is expired.");
        if (!r.HasPrimaryLocation) return TransitionResult.Blocked("Cannot activate: no primary location.");
        return TransitionResult.Ok;
    }

    /// <summary>Map a settled onboarding state onto the coarse provider status (routability).</summary>
    public static ProviderStatus ToProviderStatus(OnboardingState state) => state switch
    {
        OnboardingState.Activated => ProviderStatus.Active,
        OnboardingState.Terminated => ProviderStatus.Terminated,
        OnboardingState.Suspended => ProviderStatus.Suspended,
        _ => ProviderStatus.Suspended,   // pre-activation is never routable
    };
}
