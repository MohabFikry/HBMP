using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Signing/encryption key provisioning for the issuer (phase 12 go-live hardening; the follow-up
/// ADR-0015 parked for Phase 12). Dev/test use EPHEMERAL keys (regenerated per process, zero friction).
/// Production uses PERSISTENT RS256 keys delivered by OpenBao/Vault (transit-exported or KV, mounted
/// as PEM files or supplied via config) — stable across restarts so the published JWKS is stable and
/// tokens survive a pod recycle. There is NO dev-certificate fallback outside Development: a
/// misconfigured prod issuer FAILS FAST rather than silently signing with a throwaway self-signed cert
/// (matches the repo's fail-closed secret policy, 16.1).
///
/// Config (production), keys provisioned by OpenBao at deploy time:
///   Issuer:SigningKeyPem  / Issuer:SigningKeyPemPath      (RSA private key, PKCS#8/PEM)
///   Issuer:EncryptionKeyPem / Issuer:EncryptionKeyPemPath (RSA private key, PKCS#8/PEM)
///   Issuer:SigningKeyId    / Issuer:EncryptionKeyId       (optional stable kid; default derived)
/// </summary>
internal static class IssuerKeys
{
    public static void Configure(OpenIddictServerBuilder o, IConfiguration config, bool isDevelopment)
    {
        if (isDevelopment)
        {
            // Development PREFERS configured keys when they are present, and only falls back to ephemeral ones
            // when they are not — so `docker compose up` still works with no configuration, while a dev
            // environment that wants stability can have it.
            //
            // WHY THIS MATTERS MORE THAN IT LOOKS. An ephemeral ENCRYPTION key is what wraps refresh tokens
            // (JWE), so every restart of this service made every outstanding refresh token undecryptable:
            // `grant_type=refresh_token` answered `invalid_grant` / "The specified token is invalid", the SPA's
            // silent renew failed, and the user was signed out at the 5-minute access-token expiry. That reads
            // as "the session length is far too short" when the session length was never the problem. The
            // ephemeral SIGNING key has a second cost: its `kid` changes on every restart, so Kong's pinned
            // public key goes stale and the gateway answers "Invalid signature" until someone re-runs
            // jwks-to-pem.py. Persist both and the whole class of symptom disappears.
            var devSigning = LoadRsa(config, "SigningKey", required: false);
            var devEncryption = LoadRsa(config, "EncryptionKey", required: false);

            // Both or neither: a persistent signing key with an ephemeral encryption key would keep the gateway
            // happy while still logging everybody out on restart, which is the confusing half-fix.
            if (devSigning is not null && devEncryption is not null)
            {
                o.AddSigningKey(new RsaSecurityKey(devSigning)
                {
                    KeyId = config["Issuer:SigningKeyId"] ?? Kid(devSigning),
                });
                o.AddEncryptionKey(new RsaSecurityKey(devEncryption)
                {
                    KeyId = config["Issuer:EncryptionKeyId"] ?? Kid(devEncryption),
                });
                return;
            }

            devSigning?.Dispose();
            devEncryption?.Dispose();
            o.AddEphemeralEncryptionKey();  // wraps auth codes / refresh tokens (JWE)
            o.AddEphemeralSigningKey();      // RS256 access-token signature, published via JWKS
            return;
        }

        var signing = LoadRsa(config, "SigningKey", required: true)!;
        o.AddSigningKey(new RsaSecurityKey(signing)
        {
            KeyId = config["Issuer:SigningKeyId"] ?? Kid(signing),
        });

        // Access-token encryption is disabled (services validate a plain signed JWT), but OpenIddict
        // still needs an encryption key to protect authorization codes / refresh tokens at rest.
        var encryption = LoadRsa(config, "EncryptionKey", required: true)!;
        o.AddEncryptionKey(new RsaSecurityKey(encryption)
        {
            KeyId = config["Issuer:EncryptionKeyId"] ?? Kid(encryption),
        });
    }

    internal static RSA? LoadRsa(IConfiguration config, string name, bool required)
    {
        var pem = config[$"Issuer:{name}Pem"];
        if (string.IsNullOrWhiteSpace(pem))
        {
            var path = config[$"Issuer:{name}PemPath"];
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                pem = File.ReadAllText(path);
        }

        if (string.IsNullOrWhiteSpace(pem))
        {
            if (!required) return null;
            throw new InvalidOperationException(
                $"Issuer:{name}Pem/{name}PemPath is not configured. Production requires persistent RS256 " +
                "keys from OpenBao — there is no dev-certificate fallback outside Development.");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048)
            throw new InvalidOperationException($"Issuer:{name} must be at least RSA-2048 (got {rsa.KeySize}).");
        return rsa;
    }

    /// <summary>A stable key id derived from the public key so the JWKS `kid` is deterministic.</summary>
    internal static string Kid(RSA rsa)
    {
        var spki = rsa.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki))[..16].ToLowerInvariant();
    }
}
