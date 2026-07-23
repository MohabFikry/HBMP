using FluentAssertions;
using Mersal.Provider.Domain;

namespace Mersal.Provider.Tests;

public class OnboardingWorkflowTests
{
    private static OnboardingWorkflow.Readiness Ready(bool loc = true, bool creds = true, bool credsValid = true, bool contract = true)
        => new(loc, creds, credsValid, contract);

    [Fact]
    public void Happy_path_forward_transitions_allowed()
    {
        OnboardingWorkflow.CanTransition(OnboardingState.Draft, OnboardingState.DocumentsCollected, Ready()).Allowed.Should().BeTrue();
        OnboardingWorkflow.CanTransition(OnboardingState.DocumentsCollected, OnboardingState.Credentialed, Ready()).Allowed.Should().BeTrue();
        OnboardingWorkflow.CanTransition(OnboardingState.Credentialed, OnboardingState.Contracted, Ready()).Allowed.Should().BeTrue();
        OnboardingWorkflow.CanTransition(OnboardingState.Contracted, OnboardingState.Activated, Ready()).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Cannot_collect_documents_without_primary_location()
        => OnboardingWorkflow.CanTransition(OnboardingState.Draft, OnboardingState.DocumentsCollected, Ready(loc: false))
            .Allowed.Should().BeFalse();

    [Fact]
    public void Cannot_activate_without_active_contract()
    {
        var r = OnboardingWorkflow.CanTransition(OnboardingState.Contracted, OnboardingState.Activated, Ready(contract: false));
        r.Allowed.Should().BeFalse();
        r.Reason.Should().Contain("contract");
    }

    [Fact]
    public void Cannot_activate_with_expired_mandatory_credential()
        => OnboardingWorkflow.GuardActivation(Ready(credsValid: false)).Allowed.Should().BeFalse();

    [Fact]
    public void Suspend_and_terminate_reachable_from_activated()
    {
        OnboardingWorkflow.CanTransition(OnboardingState.Activated, OnboardingState.Suspended, Ready()).Allowed.Should().BeTrue();
        OnboardingWorkflow.CanTransition(OnboardingState.Activated, OnboardingState.Terminated, Ready()).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Illegal_jump_is_denied()
        => OnboardingWorkflow.CanTransition(OnboardingState.Draft, OnboardingState.Activated, Ready()).Allowed.Should().BeFalse();

    [Fact]
    public void Reactivation_from_suspended_requires_full_readiness()
    {
        OnboardingWorkflow.CanTransition(OnboardingState.Suspended, OnboardingState.Activated, Ready()).Allowed.Should().BeTrue();
        OnboardingWorkflow.CanTransition(OnboardingState.Suspended, OnboardingState.Activated, Ready(contract: false)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Provider_status_mapping()
    {
        OnboardingWorkflow.ToProviderStatus(OnboardingState.Activated).Should().Be(ProviderStatus.Active);
        OnboardingWorkflow.ToProviderStatus(OnboardingState.Draft).Should().Be(ProviderStatus.Suspended);
        OnboardingWorkflow.ToProviderStatus(OnboardingState.Terminated).Should().Be(ProviderStatus.Terminated);
    }
}
