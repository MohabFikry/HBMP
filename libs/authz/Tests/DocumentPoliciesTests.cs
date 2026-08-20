using FluentAssertions;
using Mersal.Audit.Client;
using Mersal.Auth;

namespace Mersal.Authz.Tests;

/// <summary>16.6 (H9): document metadata reads are role + tenant gated and audited. Proves a reader role
/// (reception) is allowed and audited (sensitive PHI access), a non-reader (finance) is denied and audited,
/// and a cross-tenant reader is denied — closing the "any document:write holder lists any beneficiary" IDOR.</summary>
public class DocumentPoliciesTests
{
    private readonly InMemoryAuditOutbox _outbox = new();

    private DefaultAuthorizationEngine Engine() =>
        new(DocumentPolicies.Bundle(), new AuditClient(_outbox, new AuditClientContext("test"), TimeProvider.System),
            NullBreakGlassProvider.Instance, TimeProvider.System);

    private static HbmpPrincipal Principal(string role, string tenant = "t0") => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string>(),
        TenantId = tenant, MfaSatisfied = true,
    };

    private static AuthzRequest Read(HbmpPrincipal p, string resourceTenant = "t0") =>
        new(p, DocumentPolicies.Read, new ResourceRef
        {
            Type = DocumentPolicies.Resource, Id = "b-1", TenantId = resourceTenant, BeneficiaryId = "b-1",
        }, "document-read");

    [Fact]
    public async Task Reception_may_read_and_the_phi_access_is_audited()
    {
        var d = await Engine().EvaluateAsync(Read(Principal("reception")));

        d.IsAllowed.Should().BeTrue();
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Allow"); // Sensitive ⇒ allow audited
    }

    [Fact]
    public async Task Finance_is_denied_and_audited()
    {
        var d = await Engine().EvaluateAsync(Read(Principal("finance")));

        d.IsAllowed.Should().BeFalse();
        d.ReasonCode.Should().Be("role-not-permitted");
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task Cross_tenant_reader_is_denied()
    {
        var d = await Engine().EvaluateAsync(Read(Principal("reception", tenant: "t0"), resourceTenant: "t9"));

        d.IsAllowed.Should().BeFalse();
    }

    // ---- operational documents (bulk uploads, error reports, extracts) -------------------------------
    //
    // The download of their BYTES required only a valid token while the upload required document:write.
    // All three kinds are PHI-bearing — an error report quotes hundreds of member numbers — so any
    // authenticated caller in the tenant could enumerate ids and stream lists of identified people.

    private static AuthzRequest OperationalRead(HbmpPrincipal p, string resourceTenant = "t0") =>
        new(p, DocumentPolicies.OperationalRead, new ResourceRef
        {
            Type = DocumentPolicies.Resource, Id = "od-1", TenantId = resourceTenant,
        }, "operational-document-read");

    private static HbmpPrincipal WithScope(string role, string scope, string tenant = "t0") => new()
    {
        Subject = "u-1", Roles = new HashSet<string> { role }, Scopes = new HashSet<string> { scope },
        TenantId = tenant, MfaSatisfied = true,
    };

    [Fact]
    public async Task Bulk_operator_may_download_an_operational_document_and_it_is_audited()
    {
        // document:write belongs to beneficiary_mgmt and beneficiary_mgmt_supervisor alone (identity
        // migration 0016) — the roles that RUN bulk membership operations and therefore produce these files.
        var d = await Engine().EvaluateAsync(OperationalRead(WithScope("beneficiary_mgmt", "document:write")));

        d.IsAllowed.Should().BeTrue();
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Allow");
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_bulk_scope_is_denied_and_audited()
    {
        // The case that was open: signed in, in the right tenant, no business with a bulk error report.
        var d = await Engine().EvaluateAsync(OperationalRead(Principal("reception")));

        d.IsAllowed.Should().BeFalse();
        _outbox.Events.Should().ContainSingle().Which.DecisionOutcome.Should().Be("Deny");
    }

    [Fact]
    public async Task A_bulk_operator_from_another_tenant_is_denied()
    {
        var d = await Engine().EvaluateAsync(
            OperationalRead(WithScope("beneficiary_mgmt", "document:write", tenant: "t0"), resourceTenant: "t9"));

        d.IsAllowed.Should().BeFalse();
    }
}
