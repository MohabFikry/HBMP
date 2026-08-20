using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Mersal.Identity.Api.Auth;

namespace Mersal.Identity.Tests;

/// <summary>
/// Phase 28.15 — a staff member's avatar.
///
/// <para>
/// ============================================================================================================
/// WHAT THESE PROVE
/// ============================================================================================================
/// Storing an uploaded file is where a web application usually acquires its first serious vulnerability, so
/// these are weighted towards the refusals rather than the happy path:
/// </para>
///
/// <list type="bullet">
///   <item>the MAGIC BYTES decide the type, not the header — a `Content-Type: image/png` on a script is the
///         oldest trick in the book, and the bytes are what a browser would act on;</item>
///   <item>an oversized body is refused rather than truncated, and refused without being read whole;</item>
///   <item>`admin:read` cannot write. The group policy is satisfied by any `admin:*` scope, so without the
///         per-handler check a read-only token could replace somebody's photograph;</item>
///   <item>the response carries `nosniff`, so stored bytes cannot be reinterpreted on the way out.</item>
/// </list>
/// </summary>
[Collection("identity-db")]
public class UserPhotoTests(IdentityHostFixture host) : IClassFixture<IdentityHostFixture>
{
    private const string Pass = "Test-Passw0rd!";
    private const string AdminScope = "openid admin:read admin:write";

    /// <summary>The smallest valid PNG signature followed by filler — enough for the sniffer, which reads
    /// the first eight bytes and does not decode the image.</summary>
    private static byte[] Png(int size = 64)
    {
        var b = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        return b;
    }

    private static ByteArrayContent Body(byte[] bytes, string declared = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(declared);
        return content;
    }

    [SkippableFact]
    public async Task A_person_sets_their_own_photo_and_reads_it_back()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"photo-self-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            var client = await SignedIn(name);
            (await client.PutAsync("/identity/me/photo", Body(Png()))).StatusCode.Should().Be(HttpStatusCode.OK);

            var read = await client.GetAsync($"/identity/users/{id}/photo");
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            read.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            // Without this a browser may re-interpret the bytes it was handed, which is the whole reason the
            // upload path bothers to sniff them.
            read.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    [SkippableFact]
    public async Task An_account_with_no_photo_answers_404_rather_than_a_placeholder()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"photo-none-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            var client = await SignedIn(name);
            // "There is no photo" is a fact the client acts on by rendering initials. Serving a placeholder
            // would make that decision on the server, for every caller, for ever.
            (await client.GetAsync($"/identity/users/{id}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    [SkippableFact]
    public async Task A_file_that_is_not_an_image_is_refused_however_it_labels_itself()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"photo-liar-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            var client = await SignedIn(name);
            // An HTML document announcing itself as a PNG. The header is a claim by the uploader; the bytes
            // are what a browser would act on, and they are what decides here.
            var html = System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>");
            var res = await client.PutAsync("/identity/me/photo", Body(html, "image/png"));

            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadAsStringAsync()).Should().Contain("photo-not-an-image");
            (await client.GetAsync($"/identity/users/{id}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    [SkippableFact]
    public async Task An_oversized_photo_is_refused_rather_than_truncated()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"photo-big-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            var client = await SignedIn(name);
            // One byte over. Truncating to the cap would store a valid-looking image the uploader did not
            // send, which is worse than refusing.
            var res = await client.PutAsync("/identity/me/photo", Body(Png(PhotoEndpoints.MaxBytes + 1)));

            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadAsStringAsync()).Should().Contain("photo-too-large");
            (await client.GetAsync($"/identity/users/{id}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    [SkippableFact]
    public async Task A_photo_can_be_removed_and_removing_a_missing_one_is_not_an_error()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var name = $"photo-del-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            var client = await SignedIn(name);
            await client.PutAsync("/identity/me/photo", Body(Png()));

            (await client.DeleteAsync("/identity/me/photo")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await client.GetAsync($"/identity/users/{id}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            // Idempotent: the caller asked for "no photo" and that is the state either way.
            (await client.DeleteAsync("/identity/me/photo")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally { await TestFlow.DeleteUser(host.Factory, id); }
    }

    [SkippableFact]
    public async Task An_administrator_can_set_a_photo_for_somebody_else()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var adminName = $"photo-admin-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(host.Factory, adminName, Pass, ["super_admin"], twoFactor: true);
        var name = $"photo-subject-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            var token = await TestFlow.AuthCodeToken(host.Factory, adminName, Pass, key, AdminScope);
            var client = host.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            // Somebody sends a headshot to HR and does not administer their own account. Ordinary, and
            // audited with both the actor and the subject.
            (await client.PutAsync($"/identity/admin/users/{id}/photo", Body(Png())))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync($"/identity/users/{id}/photo")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await TestFlow.DeleteUser(host.Factory, id);
            await TestFlow.DeleteUser(host.Factory, adminId);
        }
    }

    /// <summary>
    /// THE ONE THE GROUP POLICY DOES NOT COVER.
    ///
    /// <para>`/identity/admin` requires an `admin:*` scope and MFA — and `admin:read` satisfies that. Without
    /// the per-handler check, a read-only administrative token could replace a colleague's photograph.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_read_only_admin_token_cannot_set_somebody_elses_photo()
    {
        Skip.If(IdentityTestDb.Conn is null);
        var adminName = $"photo-ro-{Guid.NewGuid():N}";
        var (adminId, key) = await TestFlow.SeedUser(host.Factory, adminName, Pass, ["super_admin"], twoFactor: true);
        var name = $"photo-victim-{Guid.NewGuid():N}";
        var (id, _) = await TestFlow.SeedUser(host.Factory, name, Pass, ["reception"]);
        try
        {
            // admin:read ONLY — enough for the group, deliberately not enough for the write.
            var token = await TestFlow.AuthCodeToken(host.Factory, adminName, Pass, key, "openid admin:read");
            var client = host.Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var res = await client.PutAsync($"/identity/admin/users/{id}/photo", Body(Png()));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.GetAsync($"/identity/users/{id}/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await TestFlow.DeleteUser(host.Factory, id);
            await TestFlow.DeleteUser(host.Factory, adminId);
        }
    }

    [SkippableFact]
    public async Task An_anonymous_caller_cannot_read_a_photo()
    {
        Skip.If(IdentityTestDb.Conn is null);
        // Not public: it is a staff directory face, readable by colleagues rather than by the internet.
        var res = await host.Factory.CreateClient().GetAsync($"/identity/users/{Guid.NewGuid()}/photo");
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    /// <summary>A client holding an ordinary (non-admin) bearer for the named account.</summary>
    private async Task<HttpClient> SignedIn(string username)
    {
        var token = await TestFlow.AuthCodeToken(host.Factory, username, Pass, null, "openid");
        var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    /// <summary>The sniffer as a unit — the three it accepts and the shapes that nearly pass.</summary>
    [Fact]
    public void The_sniffer_recognises_exactly_the_three_formats_the_column_allows()
    {
        PhotoEndpoints.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).Should().Be("image/png");
        PhotoEndpoints.Sniff([0xFF, 0xD8, 0xFF, 0xE0]).Should().Be("image/jpeg");
        PhotoEndpoints.Sniff("RIFF____WEBP"u8).Should().Be("image/webp");

        // A truncated PNG signature is not a PNG. Length is checked before the bytes, so a short buffer
        // cannot walk off the end either.
        PhotoEndpoints.Sniff([0x89, 0x50, 0x4E]).Should().BeNull();
        // "RIFF" alone is a container header shared with WAV — the WEBP tag four bytes later is what decides.
        PhotoEndpoints.Sniff("RIFF____WAVE"u8).Should().BeNull();
        PhotoEndpoints.Sniff("<svg xmlns="u8).Should().BeNull();
        PhotoEndpoints.Sniff([]).Should().BeNull();
    }
}
