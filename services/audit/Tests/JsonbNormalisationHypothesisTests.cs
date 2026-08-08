using FluentAssertions;
using Mersal.Audit.Client;

namespace Mersal.Audit.Tests;

/// <summary>
/// DIAGNOSTIC — the hypothesis under test for the integrity.mismatch in partitions 202607/202608.
///
/// <para><b>Hypothesis.</b> <c>before_state</c> and <c>after_state</c> are <c>jsonb</c> columns. Postgres
/// re-renders jsonb on read: it inserts a space after every <c>:</c> and SORTS OBJECT KEYS. The record's
/// hash is computed at ingest over the COMPACT string the emitting service wrote (System.Text.Json emits no
/// spaces); the verifier recomputes it over whatever Postgres hands back. The two strings differ, so the
/// hashes differ, and the verifier reports "record was tampered" for a record nobody touched.</para>
///
/// <para>The decisive check is not that the hashes differ — the verifier already says that. It is that
/// recomputing with the COMPACT form reproduces the STORED hash EXACTLY. Only the real pre-image can do
/// that, so a match identifies the ingest-time bytes beyond doubt.</para>
/// </summary>
public class JsonbNormalisationHypothesisTests
{
    // The real broken record from partition 202607, index 28, read from audit.audit_event.
    private const string StoredHash = "29f901b471db416197dd97aedc28abb2978be947d053f6d558e7793e9644482c";
    private const string PrevHash = "b4d5016f13eb41bad508f1bdb45e6400ae36b2448c575e0b59d57e9e59440b9a";

    /// <summary>Exactly what Postgres returns for the jsonb columns — note the space after the colon.</summary>
    private const string BeforeAsPostgresReturnsIt = """{"caseNo": "CASE-EDITED-0523"}""";
    private const string AfterAsPostgresReturnsIt = """{"caseNo": "CASE-EDITED-4445"}""";

    /// <summary>What System.Text.Json would have emitted at ingest — no space.</summary>
    private const string BeforeAsWritten = """{"caseNo":"CASE-EDITED-0523"}""";
    private const string AfterAsWritten = """{"caseNo":"CASE-EDITED-4445"}""";

    private static AuditEvent Record(string? before, string? after) => new()
    {
        AuditEventId = Guid.Parse("0d9945de-46f5-45ff-875d-7e697acac65f"),
        ServiceName = "patient-service",
        SourceService = "patient-service",
        EntityType = "beneficiary",
        EntityId = "d5293039-8538-4778-8d0b-c9f7379e36cb",
        Action = AuditAction.Update,
        Severity = AuditSeverity.Info,
        ActorUserId = "e77f18c6-819c-4910-8b94-4a6872fbb9b2",
        ActorRole = null,
        TenantId = "11111111-1111-1111-1111-111111111111",
        ProviderId = null,
        SessionId = null,
        ActorMfa = false,
        BeforeState = before,
        AfterState = after,
        FieldClasses = ["identity", "pii"],
        DecisionOutcome = "corrected",
        DecisionPolicyId = null,
        DecisionReasonCode = null,
        Purpose = null,
        BreakGlass = false,
        CorrelationId = "dee1ddc52266177bcf3d775c3079e3fe",
        OccurredAt = DateTimeOffset.Parse("2026-07-31T14:23:04.882049+00:00"),
        PrevHash = PrevHash,
    };

    [Fact]
    public void The_compact_json_reproduces_the_STORED_hash_exactly()
    {
        // THE decisive check. Only the true pre-image reproduces a SHA-256, so an exact match proves the
        // record was ingested with COMPACT json — i.e. it was never tampered with, and the mismatch is
        // Postgres re-rendering jsonb on the way out.
        var asWritten = Record(BeforeAsWritten, AfterAsWritten);

        HashChain.ComputeRecordHash(asWritten).Should().Be(StoredHash,
            "the record is intact; the verifier is comparing against a re-rendered pre-image");
    }

    [Fact]
    public void The_postgres_rendering_reproduces_the_verifiers_RECOMPUTED_hash()
    {
        // The other half: what the verifier actually computed, and why it alarmed.
        var asRead = Record(BeforeAsPostgresReturnsIt, AfterAsPostgresReturnsIt);

        HashChain.ComputeRecordHash(asRead).Should()
            .Be("bc349272d1ec6b1e49e1afc8dd9f2c261487d790dc7840a350238740eaaeadac");
    }

    [Fact]
    public void One_added_space_is_the_whole_difference()
    {
        // The two strings differ by a single character. That is all it takes — which is the property the
        // hash chain is FOR, and exactly why the storage layer must not rewrite what was hashed.
        BeforeAsPostgresReturnsIt.Replace(": ", ":").Should().Be(BeforeAsWritten);
        HashChain.ComputeRecordHash(Record(BeforeAsWritten, AfterAsWritten)).Should()
            .NotBe(HashChain.ComputeRecordHash(Record(BeforeAsPostgresReturnsIt, AfterAsPostgresReturnsIt)));
    }
}
