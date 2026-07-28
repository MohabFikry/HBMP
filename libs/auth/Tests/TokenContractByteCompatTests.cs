using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mersal.Auth;

namespace Mersal.Auth.Tests;

/// <summary>
/// 21.0 — the byte-compat guard on the FROZEN access-token contract (docs/security/token-contract.md,
/// ADR-0015 + ADR-0021).
///
/// Phase 21 adds three claims (<c>membership_id</c>, <c>level</c>, <c>features</c>). ADR-0021 says the
/// contract is extended ADDITIVELY and never broken, which is a claim about BYTES, not about intent — so
/// these fixtures are literal encoded JWTs, checked in verbatim. The legacy fixture is a token shaped
/// exactly as the issuer minted them BEFORE phase 21; it must keep parsing into the same
/// <see cref="HbmpPrincipal"/> it always did, with the new properties at their absent defaults.
///
/// If a future change breaks that, this test goes red on the OLD fixture — which is the whole point:
/// every access token already in flight at deploy time is a legacy token, and 300 s of them survive the
/// rollout (token-contract.md §4).
///
/// Signatures are NOT validated here by design — signature/issuer/audience validation is
/// <c>IssuerConformanceTests</c>'s job against the live issuer. This test pins the CLAIM SHAPE, so the
/// fixture bytes stay stable and readable rather than being re-signed on every key rotation.
/// </summary>
public class TokenContractByteCompatTests
{
    /// <summary>A token as minted BEFORE phase 21 — the frozen §2 claims and nothing else. Never regenerate
    /// this: its whole value is that it is old bytes.</summary>
    private const string LegacyToken =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6ImhibXAtc2lnLTEifQ." +
        "eyJpc3MiOiJodHRwOi8vaWRlbnRpdHktc2VydmljZTo4MDgwIiwiYXVkIjoiaGJtcC1hcGkiLCJleHAiOjE5MDAwMDAwMDAs" +
        "ImlhdCI6MTg5OTk5OTcwMCwibmJmIjoxODk5OTk5NzAwLCJzdWIiOiI3YzViMGEyZS0yZjYxLTRhOWQtOGY3YS0xYjZlMmQz" +
        "YzRhNTUiLCJyb2xlcyI6WyJkb2N0b3IiLCJyZWNlcHRpb24iXSwic2NvcGUiOiJlbXI6cmVhZCBlbXI6d3JpdGUgb3JkZXJz" +
        "OnJlYWQgcmVjZXB0aW9uOnNlYXJjaCIsInRlbmFudF9pZCI6Im1lcnNhbC1lZyIsInByb3ZpZGVyX2lkIjoiM2YyNTA0ZTAt" +
        "NGY4OS0xMWQzLTlhMGMtMDMwNWU4MmMzMzAxIiwic2lkIjoiOWIxZGViNGQtM2I3ZC00YmFkLTliZGQtMmIwZDdiM2RjYjZk" +
        "Iiwic3JjX2lwIjoiMTk3LjUxLjEwMC4yNCIsImFtciI6WyJwd2QiLCJvdHAiXSwiYWNyIjoiYWFsMiIsIm5hbWUiOiJEci4g" +
        "WWFzbWluZSBBYmRlbC1SYWhtYW4iLCJwcmVmZXJyZWRfdXNlcm5hbWUiOiJ5LmFiZGVscmFobWFuIn0." +
        "c2lnbmF0dXJlLW5vdC12YWxpZGF0ZWQtYnktdGhpcy1maXh0dXJl";

    /// <summary>The same token plus the three phase-21 claims — identical in every frozen field.</summary>
    private const string Phase21Token =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6ImhibXAtc2lnLTEifQ." +
        "eyJpc3MiOiJodHRwOi8vaWRlbnRpdHktc2VydmljZTo4MDgwIiwiYXVkIjoiaGJtcC1hcGkiLCJleHAiOjE5MDAwMDAwMDAs" +
        "ImlhdCI6MTg5OTk5OTcwMCwibmJmIjoxODk5OTk5NzAwLCJzdWIiOiI3YzViMGEyZS0yZjYxLTRhOWQtOGY3YS0xYjZlMmQz" +
        "YzRhNTUiLCJyb2xlcyI6WyJkb2N0b3IiLCJyZWNlcHRpb24iXSwic2NvcGUiOiJlbXI6cmVhZCBlbXI6d3JpdGUgb3JkZXJz" +
        "OnJlYWQgcmVjZXB0aW9uOnNlYXJjaCIsInRlbmFudF9pZCI6Im1lcnNhbC1lZyIsInByb3ZpZGVyX2lkIjoiM2YyNTA0ZTAt" +
        "NGY4OS0xMWQzLTlhMGMtMDMwNWU4MmMzMzAxIiwic2lkIjoiOWIxZGViNGQtM2I3ZC00YmFkLTliZGQtMmIwZDdiM2RjYjZk" +
        "Iiwic3JjX2lwIjoiMTk3LjUxLjEwMC4yNCIsImFtciI6WyJwd2QiLCJvdHAiXSwiYWNyIjoiYWFsMiIsIm5hbWUiOiJEci4g" +
        "WWFzbWluZSBBYmRlbC1SYWhtYW4iLCJwcmVmZXJyZWRfdXNlcm5hbWUiOiJ5LmFiZGVscmFobWFuIiwibWVtYmVyc2hpcF9p" +
        "ZCI6ImExZThmNGMyLTlkM2ItNGU1Ny04YTE2LTVjN2I5ZTBkMmYzOCIsImxldmVsIjozLCJmZWF0dXJlcyI6WyJjbGFpbXMi" +
        "LCJjYWxsY2VudHJlIiwicmVwb3J0aW5nX2V4dHJhY3RzIl19." +
        "c2lnbmF0dXJlLW5vdC12YWxpZGF0ZWQtYnktdGhpcy1maXh0dXJl";

    [Fact]
    public void Legacy_token_still_yields_the_same_principal_it_always_did()
    {
        var p = PrincipalFrom(LegacyToken);

        // Every frozen §2 claim, unchanged.
        p.Subject.Should().Be("7c5b0a2e-2f61-4a9d-8f7a-1b6e2d3c4a55");
        p.Roles.Should().BeEquivalentTo("doctor", "reception");
        p.Scopes.Should().BeEquivalentTo("emr:read", "emr:write", "orders:read", "reception:search");
        p.TenantId.Should().Be("mersal-eg");
        p.ProviderId.Should().Be("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        p.SessionId.Should().Be("9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d");
        p.SourceIp.Should().Be("197.51.100.24");
        p.Acr.Should().Be("aal2");
        p.Amr.Should().BeEquivalentTo("pwd", "otp");
        p.MfaSatisfied.Should().BeTrue();
        p.DisplayName.Should().Be("Dr. Yasmine Abdel-Rahman");
    }

    [Fact]
    public void Legacy_token_leaves_the_phase21_claims_absent_not_defaulted_to_authority()
    {
        var p = PrincipalFrom(LegacyToken);

        // THE additive guarantee: absent must mean ABSENT. A missing membership must not silently read as
        // "some membership", and a missing level must not read as 0 — 0 is the MOST privileged tier, so
        // defaulting it would hand every legacy token platform authority (design 40 §2, invariant 2).
        p.MembershipId.Should().BeNull();
        p.Level.Should().BeNull();
        p.Features.Should().BeEmpty();
        p.HasFeature("claims").Should().BeFalse();
    }

    [Fact]
    public void Phase21_token_carries_membership_level_and_features()
    {
        var p = PrincipalFrom(Phase21Token);

        p.MembershipId.Should().Be("a1e8f4c2-9d3b-4e57-8a16-5c7b9e0d2f38");
        p.Level.Should().Be(3);
        p.Features.Should().BeEquivalentTo("claims", "callcentre", "reporting_extracts");
        p.HasFeature("callcentre").Should().BeTrue();
        p.HasFeature("interop").Should().BeFalse("a feature absent from the claim is NOT enabled");
    }

    [Fact]
    public void Adding_the_phase21_claims_changes_nothing_frozen()
    {
        // The additive property stated as an equality: strip the three new properties and the two fixtures
        // must be indistinguishable. This is what "extended additively, never broken" means operationally.
        var legacy = PrincipalFrom(LegacyToken);
        var phase21 = PrincipalFrom(Phase21Token);

        var frozen = (HbmpPrincipal p) => p with { MembershipId = null, Level = null, Features = new HashSet<string>(StringComparer.Ordinal) };

        frozen(phase21).Should().BeEquivalentTo(frozen(legacy));
    }

    [Fact]
    public void Level_that_is_not_an_integer_is_absent_rather_than_zero()
    {
        // Fail closed on a malformed tier: an unparseable level must not collapse to 0 (most privileged).
        var user = Principal(new Claim("sub", "u"), new Claim(HbmpClaimTypes.Level, "not-a-number"));

        HbmpPrincipal.FromClaims(user).Level.Should().BeNull();
    }

    /// <summary>Decode a JWT's payload into a principal exactly as the JwtBearer handler does with
    /// <c>MapInboundClaims = false</c> (libs/auth/ServiceCollectionExtensions.cs): raw JSON claim names,
    /// one repeated Claim per array element. No signature validation — see the class remarks.</summary>
    private static HbmpPrincipal PrincipalFrom(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var json = Encoding.UTF8.GetString(Base64UrlDecode(payload));
        using var doc = JsonDocument.Parse(json);

        var claims = new List<Claim>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in prop.Value.EnumerateArray())
                {
                    claims.Add(new Claim(prop.Name, el.ToString()));
                }
            }
            else
            {
                claims.Add(new Claim(prop.Name, prop.Value.ToString()));
            }
        }

        return HbmpPrincipal.FromClaims(Principal([.. claims]));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
