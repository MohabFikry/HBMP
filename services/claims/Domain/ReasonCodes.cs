namespace Mersal.Claims.Domain;

/// <summary>The complete claim denial / reason-code catalogue (22 §11.5). Adjudication collects <b>all</b> applicable
/// codes per line (never stopping at the first failure) so partial approvals are precise. Every code the engine
/// emits must be in <see cref="All"/>; a reason-code-completeness test asserts both directions.</summary>
public static class ReasonCodes
{
    public const string NotEligible = "NOT_ELIGIBLE";
    public const string PolicyExpired = "POLICY_EXPIRED";
    public const string NotCoveredCategory = "NOT_COVERED_CATEGORY";
    public const string NoPriorAuth = "NO_PRIOR_AUTH";
    public const string AuthExpired = "AUTH_EXPIRED";
    public const string ExceedsAuthScope = "EXCEEDS_AUTH_SCOPE";
    public const string NoFulfillmentRecord = "NO_FULFILLMENT_RECORD";
    public const string DuplicateClaim = "DUPLICATE_CLAIM";
    public const string ProviderOutOfNetwork = "PROVIDER_OUT_OF_NETWORK";
    public const string ContractNotEffective = "CONTRACT_NOT_EFFECTIVE";
    public const string NoTariff = "NO_TARIFF";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string NotMedicallyNecessary = "NOT_MEDICALLY_NECESSARY";
    public const string IllegibleDocument = "ILLEGIBLE_DOCUMENT";
    public const string ReceiptMismatch = "RECEIPT_MISMATCH";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NotEligible, PolicyExpired, NotCoveredCategory, NoPriorAuth, AuthExpired, ExceedsAuthScope,
        NoFulfillmentRecord, DuplicateClaim, ProviderOutOfNetwork, ContractNotEffective, NoTariff,
        LimitExceeded, NotMedicallyNecessary, IllegibleDocument, ReceiptMismatch,
    };

    public static bool IsKnown(string code) => All.Contains(code);
}
