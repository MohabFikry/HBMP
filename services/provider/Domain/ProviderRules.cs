namespace Mersal.Provider.Domain;

/// <summary>Presentation mapping: canonical <see cref="ProviderType"/> → the business labels the
/// Network Team uses. The enum is authoritative in storage (22 §5.1); labels never persist.</summary>
public static class ProviderTypeLabels
{
    public static string Label(ProviderType t) => t switch
    {
        ProviderType.Hospital => "Hospital",
        ProviderType.Clinic => "Doctor / Clinic",
        ProviderType.Lab => "Laboratory",
        ProviderType.Pharmacy => "Pharmacy",
        // Both spellings label the same thing to a human; the string is the display, not the key.
        ProviderType.Imaging or ProviderType.Radiology => "Imaging Center",
        _ => t.ToString(),
    };
}

/// <summary>Effective-range rules for a provider's contracts (22 §5.3): a provider may hold many
/// contracts over time, but their effective ranges must not overlap.</summary>
public static class ContractRules
{
    /// <summary>Two closed/open date ranges overlap when each starts on or before the other ends.
    /// A null <c>effective_to</c> means open-ended (+∞).</summary>
    public static bool Overlaps(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo)
    {
        var aEnd = aTo ?? DateOnly.MaxValue;
        var bEnd = bTo ?? DateOnly.MaxValue;
        return aFrom <= bEnd && bFrom <= aEnd;
    }

    /// <summary>True when <paramref name="candidate"/> overlaps any existing (non-deleted) contract.</summary>
    public static bool OverlapsAny(IEnumerable<ProviderContract> existing, DateOnly from, DateOnly? to, Guid? excludeContractId = null)
        => existing.Any(c => !c.IsDeleted && c.ContractId != excludeContractId && Overlaps(c.EffectiveFrom, c.EffectiveTo, from, to));

    /// <summary>A contract is in effect on <paramref name="on"/> when Active and the date is within its range.</summary>
    public static bool InEffect(ProviderContract c, DateOnly on)
        => !c.IsDeleted && c.Status == ContractStatus.Active
           && on >= c.EffectiveFrom && (c.EffectiveTo is null || on <= c.EffectiveTo);
}

/// <summary>A single routable capability: a service_type + code a provider can fulfil under an
/// in-effect contract (FR-NET-006). agreed_price is carried separately and masked by role.</summary>
public sealed record Capability(ServiceType ServiceType, CodeSystem CodeSystem, string Code);

/// <summary>Derives which codes a provider can currently fulfil. Only an <b>Active</b> provider with an
/// <b>Active, in-effect</b> contract is routable; Suspended/Terminated providers and Draft/expired
/// contracts contribute nothing (but remain readable for audit).</summary>
public static class CapabilityDerivation
{
    public static IReadOnlyList<Capability> Derive(Provider provider, DateOnly on)
    {
        if (provider.IsDeleted || provider.Status != ProviderStatus.Active) return [];
        return provider.Contracts
            .Where(c => ContractRules.InEffect(c, on))
            .SelectMany(c => c.ServiceLines)
            .Select(l => new Capability(l.ServiceType, l.CodeSystem, l.Code))
            .Distinct()
            .ToList();
    }

    /// <summary>True when the provider can fulfil the given code under an in-effect contract.</summary>
    public static bool CanFulfil(Provider provider, CodeSystem system, string code, DateOnly on)
        => Derive(provider, on).Any(c => c.CodeSystem == system && string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Credential expiry + activation-gate rules (FR-NET-004/007).</summary>
public static class CredentialRules
{
    /// <summary>A credential counts as valid on <paramref name="on"/> when marked Valid and not past
    /// its <c>valid_to</c>.</summary>
    public static bool IsValidOn(ProviderCredential c, DateOnly on)
        => !c.IsDeleted && c.Status == CredentialStatus.Valid
           && (c.ValidFrom is null || on >= c.ValidFrom) && (c.ValidTo is null || on <= c.ValidTo);

    /// <summary>A credential is due for an expiry reminder when it will lapse within
    /// <paramref name="windowDays"/> of <paramref name="on"/> (and has not already lapsed).</summary>
    public static bool ExpiryReminderDue(ProviderCredential c, DateOnly on, int windowDays = 30)
        => c.ValidTo is { } to && !c.IsDeleted && to >= on && to <= on.AddDays(windowDays);

    /// <summary>Activation gate: every mandatory credential must be present and valid on <paramref name="on"/>.</summary>
    public static bool MandatoryCredentialsSatisfied(IEnumerable<ProviderCredential> credentials, DateOnly on)
    {
        var mandatory = credentials.Where(c => c.IsMandatory && !c.IsDeleted).ToList();
        return mandatory.Count > 0 && mandatory.All(c => IsValidOn(c, on));
    }
}
