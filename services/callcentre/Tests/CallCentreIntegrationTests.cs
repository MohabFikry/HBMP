using FluentAssertions;
using Mersal.CallCentre.Domain;
using Mersal.CallCentre.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.CallCentre.Tests;

/// <summary>callcentre-service at the datastore (env-gated <c>CALLCENTRE_TEST_DB</c>; needs the hbmp superuser
/// conn). Proves the monotonic call ref, the VERIFICATION GATE (pass binds + IsVerified true; expiry on close), that
/// a Fail never verifies, and — the privacy invariant — that ONLY identifier TYPES are persisted, never values.
/// Serialized via the callcentre-db collection. No-ops without the env var.</summary>
[Collection("callcentre-db")]
public class CallCentreIntegrationTests
{
    private static readonly string? Db = Environment.GetEnvironmentVariable("CALLCENTRE_TEST_DB");

    private static DbContextOptions<CallCentreDbContext> Options() =>
        new DbContextOptionsBuilder<CallCentreDbContext>().UseNpgsql(Db).UseSnakeCaseNamingConvention().Options;

    [SkippableFact]
    public async Task Call_ref_is_monotonic_per_year()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        await using var db = new CallCentreDbContext(Options());
        var a = await new CallRefIssuer(db).NextAsync(2026);
        var b = await new CallRefIssuer(db).NextAsync(2026);
        a.Should().StartWith("CALL-2026-");
        string.CompareOrdinal(b, a).Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Pass_binds_beneficiary_and_gate_opens_then_closing_expires_it()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
        var beneficiary = Guid.NewGuid();
        var agent = Guid.NewGuid();
        Guid interactionId;
        try
        {
            await using (var db = new CallCentreDbContext(Options()))
            {
                var i = new CallInteraction
                {
                    InteractionId = Guid.NewGuid(), CallRef = await new CallRefIssuer(db).NextAsync(2026),
                    TenantId = tenant, AgentUserId = agent, Direction = CallDirection.Inbound,
                    StartedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Interactions.Add(i);
                db.Verifications.Add(new CallerVerification
                {
                    VerificationId = Guid.NewGuid(), InteractionId = i.InteractionId, BeneficiaryId = beneficiary,
                    TenantId = tenant, VerifiedIdentifierTypes = ["MemberNo", "DateOfBirth"],
                    Result = VerificationResult.Passed, VerifiedAt = DateTimeOffset.UtcNow,
                });
                i.BeneficiaryId = beneficiary;   // pass binds the interaction
                await db.SaveChangesAsync();
                interactionId = i.InteractionId;
            }

            await using (var db = new CallCentreDbContext(Options()))
            {
                var gate = new VerificationService(db);
                (await gate.IsVerifiedAsync(interactionId, beneficiary)).Should().BeTrue();
                (await gate.BoundBeneficiaryAsync(interactionId)).Should().Be(beneficiary);
                // A different beneficiary is NOT verified on this call.
                (await gate.IsVerifiedAsync(interactionId, Guid.NewGuid())).Should().BeFalse();

                // Close the interaction → the verification expires.
                var i = await db.Interactions.FirstAsync(x => x.InteractionId == interactionId);
                i.Status = InteractionStatus.Closed;
                i.EndedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }

            await using (var db = new CallCentreDbContext(Options()))
            {
                var gate = new VerificationService(db);
                (await gate.IsVerifiedAsync(interactionId, beneficiary)).Should().BeFalse();
                (await gate.BoundBeneficiaryAsync(interactionId)).Should().BeNull();
            }
        }
        finally
        {
            await using var db = new CallCentreDbContext(Options());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = {0};", tenant);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = {0};", tenant);
        }
    }

    [SkippableFact]
    public async Task Failed_verification_never_opens_the_gate()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
        var beneficiary = Guid.NewGuid();
        Guid interactionId;
        try
        {
            await using (var db = new CallCentreDbContext(Options()))
            {
                var i = new CallInteraction
                {
                    InteractionId = Guid.NewGuid(), CallRef = await new CallRefIssuer(db).NextAsync(2026),
                    TenantId = tenant, AgentUserId = Guid.NewGuid(), Direction = CallDirection.Inbound,
                    StartedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Interactions.Add(i);
                db.Verifications.Add(new CallerVerification
                {
                    VerificationId = Guid.NewGuid(), InteractionId = i.InteractionId, BeneficiaryId = beneficiary,
                    TenantId = tenant, VerifiedIdentifierTypes = ["MemberNo"], Result = VerificationResult.Failed,
                    FailureReason = "unconfirmed", VerifiedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();   // interaction is NOT bound (no pass)
                interactionId = i.InteractionId;
            }

            await using (var db = new CallCentreDbContext(Options()))
            {
                (await new VerificationService(db).IsVerifiedAsync(interactionId, beneficiary)).Should().BeFalse();
            }
        }
        finally
        {
            await using var db = new CallCentreDbContext(Options());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = {0};", tenant);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = {0};", tenant);
        }
    }

    [SkippableFact]
    public async Task Only_identifier_types_are_persisted_never_values()
    {
        Skip.If(Db is null, "test DB not configured — set the *_TEST_DB env var to run this DB integration test.");
        var tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Guid vid;
            await using (var db = new CallCentreDbContext(Options()))
            {
                var i = new CallInteraction
                {
                    InteractionId = Guid.NewGuid(), CallRef = await new CallRefIssuer(db).NextAsync(2026),
                    TenantId = tenant, AgentUserId = Guid.NewGuid(), Direction = CallDirection.Inbound,
                    StartedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.Interactions.Add(i);
                var v = new CallerVerification
                {
                    VerificationId = Guid.NewGuid(), InteractionId = i.InteractionId, BeneficiaryId = Guid.NewGuid(),
                    TenantId = tenant, VerifiedIdentifierTypes = ["MemberNo", "Phone"],
                    Result = VerificationResult.Passed, VerifiedAt = DateTimeOffset.UtcNow,
                };
                db.Verifications.Add(v);
                await db.SaveChangesAsync();
                vid = v.VerificationId;
            }

            // Inspect the raw persisted jsonb — it must contain only the TYPE names, nothing value-shaped.
            await using (var db = new CallCentreDbContext(Options()))
            {
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT verified_identifiers::text FROM callcentre.caller_verification WHERE verification_id = @id";
                var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = vid; cmd.Parameters.Add(p);
                var raw = (string?)await cmd.ExecuteScalarAsync() ?? "";
                raw.Should().Contain("MemberNo").And.Contain("Phone");
                raw.Should().Be("[\"MemberNo\", \"Phone\"]");   // exactly the types, in a bare string array
            }
        }
        finally
        {
            await using var db = new CallCentreDbContext(Options());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.caller_verification WHERE tenant_id = {0};", tenant);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM callcentre.call_interaction WHERE tenant_id = {0};", tenant);
        }
    }
}
