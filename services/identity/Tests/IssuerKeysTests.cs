using System.Security.Cryptography;
using FluentAssertions;
using Mersal.Identity.Api.Auth;
using Microsoft.Extensions.Configuration;

namespace Mersal.Identity.Tests;

/// <summary>
/// The production signing-key path (phase 12 go-live hardening): persistent RS256 keys from OpenBao,
/// fail-fast when unconfigured, and a deterministic JWKS kid. Dev/test still use ephemeral keys.
/// </summary>
public sealed class IssuerKeysTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Production_without_keys_fails_fast_no_dev_cert_fallback()
    {
        var act = () => IssuerKeys.LoadRsa(Config([]), "SigningKey", required: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*OpenBao*no dev-certificate fallback*");
    }

    [Fact]
    public void Loads_a_pem_private_key_and_derives_a_stable_kid()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();

        using var loaded = IssuerKeys.LoadRsa(Config(new() { ["Issuer:SigningKeyPem"] = pem }), "SigningKey", required: true)!;
        loaded.Should().NotBeNull();

        // kid is derived from the PUBLIC key, so the same key always yields the same kid across restarts.
        IssuerKeys.Kid(loaded).Should().Be(IssuerKeys.Kid(rsa)).And.HaveLength(16);
    }

    [Fact]
    public void Rejects_a_key_weaker_than_rsa_2048()
    {
        using var weak = RSA.Create(1024);
        var pem = weak.ExportPkcs8PrivateKeyPem();
        var act = () => IssuerKeys.LoadRsa(Config(new() { ["Issuer:SigningKeyPem"] = pem }), "SigningKey", required: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*RSA-2048*");
    }

    [Fact]
    public void Reads_key_material_from_a_mounted_pem_file()
    {
        using var rsa = RSA.Create(2048);
        var path = Path.Combine(Path.GetTempPath(), $"issuer-key-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var loaded = IssuerKeys.LoadRsa(Config(new() { ["Issuer:SigningKeyPemPath"] = path }), "SigningKey", required: true)!;
            IssuerKeys.Kid(loaded).Should().Be(IssuerKeys.Kid(rsa));
        }
        finally { File.Delete(path); }
    }
}
