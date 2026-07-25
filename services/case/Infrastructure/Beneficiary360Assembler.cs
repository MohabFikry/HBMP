using Mersal.Case.Domain;

namespace Mersal.Case.Infrastructure;

/// <summary>Assembles the beneficiary-360 COORDINATION view (phase 10.1) by calling sibling services (eligibility/
/// policy, approvals, appointments, emr summary) under the caller's purpose (coordination) + bearer token. The
/// contract returns the field-scoped <see cref="Beneficiary360"/> DTO ONLY — never raw EMR records. The HTTP
/// implementation lives in the Api layer; tests inject a fake. Fail-closed: a null result means the coordination
/// view could not be assembled (a sibling denied or was unreachable) → the endpoint surfaces a problem, no partial
/// leak. Per 11-permission-matrix §4 the clinical portion is a summary: diagnosis coord-visible, notes/rx/results
/// masked.</summary>
public interface IBeneficiary360Assembler
{
    Task<Beneficiary360?> AssembleAsync(CaseFile @case, string? bearerToken, CancellationToken ct = default);
}
