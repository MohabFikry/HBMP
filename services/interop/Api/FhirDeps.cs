using Mersal.Interop.Infrastructure;

namespace Mersal.Interop.Api;

/// <summary>Per-request dependencies for the FHIR endpoints — the gate, the data source, the audit emitter, and
/// the idempotency-ledger context. <see cref="Bearer"/> forwards the CALLER's token to the owning services so
/// they enforce their own authorization (defense in depth).</summary>
public sealed class FhirDeps(InteropGate gate, IFhirDataSource source, FhirAudit audit, InteropDbContext db)
{
    public InteropGate Gate { get; } = gate;
    public IFhirDataSource Source { get; } = source;
    public FhirAudit Audit { get; } = audit;
    public InteropDbContext Db { get; } = db;

    public string? Bearer(HttpContext http) => http.Request.Headers.Authorization.FirstOrDefault();
}
