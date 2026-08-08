using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Identity.Api.Auth;
using Microsoft.AspNetCore.Identity;
using Mersal.Identity.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.7 — an administrator ISSUES a reset link; they no longer choose the password (ADR-0036 §6.5).
///
/// <para>
/// Either a password is a secret only its owner knows, or it is not. Shipping self-service reset while
/// leaving an endpoint that hands an administrator a working credential would answer that question both ways
/// at once — so these tests are less about the new behaviour than about the ABSENCE of the old one.
/// </para>
/// </summary>
public class AdminPasswordResetTests
{
    /// <summary>
    /// The contract no longer accepts a password, asserted from the TYPE rather than from a handler.
    ///
    /// <para>
    /// A record kept "for compatibility" is a shape somebody re-wires later, and the endpoint it belonged to
    /// has nothing left for a new password to mean. This test fails if it comes back.
    /// </para>
    /// </summary>
    [Fact]
    public void The_administrative_request_carries_a_language_and_not_a_password()
    {
        var properties = typeof(AdminEndpoints.AdminResetRequest)
            .GetProperties().Select(p => p.Name).ToArray();

        properties.Should().NotContain("NewPassword",
            "an administrator who chooses the password is an administrator who knows it");
        properties.Should().Contain("Lang");
    }

    [Fact]
    public void The_old_password_carrying_request_type_is_gone_entirely()
    {
        // Not renamed, not obsoleted — removed. Reflection over the assembly rather than a compile-time
        // reference, because a compile-time reference to a type that must not exist cannot be written.
        typeof(AdminEndpoints).Assembly.GetTypes()
            .Where(t => t.Name.Contains("ResetPasswordRequest", StringComparison.Ordinal))
            .Should().BeEmpty();
    }
}

/// <summary>
/// The administrative reset, driven end to end.
/// </summary>
[Collection("identity-db")]
public class AdminPasswordResetFlowTests : IClassFixture<IdentityAppFactory>
{
    private readonly IdentityAppFactory _factory;
    public AdminPasswordResetFlowTests(IdentityAppFactory factory) => _factory = factory;

    private const string Pass = "Test-Passw0rd!";
    private const string Scope = "openid admin:read admin:write";

    private sealed class Seeded(IdentityAppFactory factory, Guid id, string name, string? totpKey) : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string? TotpKey { get; } = totpKey;
        public async ValueTask DisposeAsync() => await TestFlow.DeleteUser(factory, Id);
    }

    private async Task<Seeded> Seed(string prefix, bool twoFactor = false, params string[] roles)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        var (id, key) = await TestFlow.SeedUser(
            _factory, name, Pass, roles.Length > 0 ? roles : ["reception"], twoFactor: twoFactor);
        return new Seeded(_factory, id, name, key);
    }

    /// <summary>An administrator with a token the admin surface will accept.
    ///
    /// The `/identity/admin` group requires an MFA SESSION, not merely the scope — deciding something about
    /// somebody else's account is exactly the kind of act that gate exists for. So the administrator here is
    /// seeded WITH a second factor and signs in through it; a token minted without one is refused 403, which
    /// is the gate working and not a defect in this endpoint.</summary>
    private async Task<HttpClient> AdminClient(Seeded admin)
    {
        var token = await TestFlow.AuthCodeToken(_factory, admin.Name, Pass, admin.TotpKey, Scope);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [SkippableFact]
    public async Task An_administrator_sends_a_link_and_never_learns_the_password()
    {
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Seed("admreset-admin", twoFactor: true, "super_admin");
        await using var target = await Seed("admreset-target");

        var client = await AdminClient(admin);
        CapturedEmail.Instance.Clear();

        var res = await client.PostAsJsonAsync(
            $"/identity/admin/users/{target.Id}/reset-password", new { lang = "en" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("resetLinkSent");
        // The reply must not carry a credential of any kind — that was the entire point of the change.
        body.Should().NotContain("password", "the administrator learns that a link went out, not what it sets");

        // And a link actually went to the USER, not to the administrator.
        var sent = CapturedEmail.Instance.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be($"{target.Name}@example.org");
    }

    [SkippableFact]
    public async Task The_link_an_administrator_sends_is_the_same_kind_a_person_asks_for()
    {
        // Shared code, asserted by behaviour: an administrative link that expired differently or verified
        // differently would be a second reset system nobody was maintaining.
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Seed("admreset-same-admin", twoFactor: true, "super_admin");
        await using var target = await Seed("admreset-same-target");

        var client = await AdminClient(admin);
        CapturedEmail.Instance.Clear();
        await client.PostAsJsonAsync($"/identity/admin/users/{target.Id}/reset-password", new { lang = "en" });

        var text = CapturedEmail.Instance.Sent.Should().ContainSingle().Subject.Text;
        var m = Regex.Match(text, @"reset-password\?u=([^&\s]+)&t=([^\s]+)");
        m.Success.Should().BeTrue();

        // Use it exactly as the person would.
        var anon = _factory.CreateClient();
        var csrf = await Antiforgery(anon);
        var req = new HttpRequestMessage(HttpMethod.Post, "/connect/password/reset")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                userId = Guid.Parse(m.Groups[1].Value),
                token = m.Groups[2].Value,
                newPassword = "Admin-Sent-Passw0rd!",
            }),
        };
        req.Headers.Add("X-HBMP-CSRF", csrf);
        (await anon.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task An_account_with_no_email_address_is_told_so_rather_than_silently_doing_nothing()
    {
        // The caller here is an authenticated administrator who already holds the user id, so bluntness costs
        // nothing and vagueness costs them the one thing they need to act on.
        Skip.If(IdentityTestDb.Conn is null);
        await using var admin = await Seed("admreset-noemail-admin", twoFactor: true, "super_admin");
        await using var target = await Seed("admreset-noemail-target");

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var u = await users.FindByIdAsync(target.Id.ToString());
            u!.Email = null;
            u.NormalizedEmail = null;
            await users.UpdateAsync(u);
        }

        var client = await AdminClient(admin);

        var res = await client.PostAsJsonAsync(
            $"/identity/admin/users/{target.Id}/reset-password", new { lang = "en" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.Content.ReadAsStringAsync()).Should().Contain("no email address");
    }

    private static async Task<string> Antiforgery(HttpClient client) =>
        (await (await client.GetAsync("/connect/session/antiforgery"))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString()!;
}
