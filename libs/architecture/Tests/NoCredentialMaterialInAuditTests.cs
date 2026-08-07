using System.Text.RegularExpressions;
using FluentAssertions;

namespace Mersal.Architecture.Tests;

/// <summary>
/// 21.5 — no credential material ever reaches the audit trail or the logs (design 40 §6, 19-audit-strategy).
///
/// The audit trail is retained for years, replicated, exported for review, and readable by people who are
/// deliberately NOT allowed to see clinical data. It is the last place a password hash, a TOTP secret or a
/// recovery code should end up — and the way they get there is never deliberate. Someone serialises "the
/// whole user object" into an AfterState, or logs the request body while debugging a login failure, and it
/// is invisible in review because the line looks like every other audit call.
///
/// The phase-21 prompt refers to this as an existing check. It was not — nothing in the repo asserted it —
/// so it is written here rather than assumed.
/// </summary>
public class NoCredentialMaterialInAuditTests
{
    /// <summary>Identifiers that carry, or plausibly carry, credential material.</summary>
    private static readonly Regex Credential = new(
        @"\b(PasswordHash|passwordHash|password_hash|NewPassword|newPassword|\bPassword\b|" +
        @"AuthenticatorKey|authenticatorKey|RecoveryCode|recoveryCode|ClientSecret|clientSecret|" +
        @"SecurityStamp|securityStamp)\b",
        RegexOptions.Compiled);

    /// <summary>Calls that publish somewhere durable and widely readable.</summary>
    private static readonly Regex PublishingCall = new(
        @"(AuditEventDraft|EmitAsync\(|\bAudit\(|AfterState\s*=|BeforeState\s*=|" +
        @"Log(Information|Warning|Error|Critical|Debug|Trace)\()",
        RegexOptions.Compiled);

    [Fact]
    public void No_production_code_puts_credential_material_into_an_audit_event_or_a_log()
    {
        var offenders = new List<string>();

        foreach (var (absolute, relative) in ProductionFiles())
        {
            var lines = File.ReadAllLines(absolute);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!PublishingCall.IsMatch(lines[i])) continue;

                // A publishing call plus its arguments — audit drafts and structured log calls routinely
                // span several lines, so checking only the matched line would miss the realistic case.
                var window = string.Join('\n', lines.Skip(i).Take(6));
                var stripped = StripCommentsAndStrings(window);

                if (Credential.IsMatch(stripped))
                    offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "credential material must never reach the audit trail or the logs — both are retained, " +
            "exported, and readable by people who may not see clinical data. Offending sites:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_scanner_actually_detects_the_thing_it_looks_for()
    {
        // A checker that cannot fail is not a check. This proves the pattern would fire on the realistic
        // mistake — serialising the user's hash into an audit payload — so a future regex "tidy-up" that
        // silently stops matching is caught here rather than by nothing at all.
        var sample = """
            await Audit(audit, me, "identity.user", id, AuditAction.Update, "UserPasswordReset",
                $"{{\"hash\":\"{user.PasswordHash}\"}}");
            """;

        PublishingCall.IsMatch(sample).Should().BeTrue();
        Credential.IsMatch(StripCommentsAndStrings(sample)).Should().BeTrue();
    }

    [Fact]
    public void The_scanner_does_not_fire_on_prose_that_merely_mentions_a_password()
    {
        // The realistic false positive, and the one that turned this check red: an INTERPOLATED log message
        // whose English names what is being reset. The only variable reaching the log is `user.Id`.
        //
        // Worth a test of its own rather than a quiet regex tweak. A guard that fires on correct code gets
        // suppressed, and the suppression outlives the false positive — so the shape that provoked it is
        // pinned here, on the side of the line where it belongs.
        var sample = """
            .LogError(ex, "Password-reset email could not be sent for user {UserId}.", user.Id);
            """;

        PublishingCall.IsMatch(sample).Should().BeTrue("it is a log call, so it is still worth scanning");
        Credential.IsMatch(StripCommentsAndStrings(sample)).Should().BeFalse(
            "the word 'Password' in a message is prose; what matters is the VARIABLES that reach the log");
    }

    [Fact]
    public void The_scanner_does_not_fire_on_a_clean_audit_call()
    {
        var sample = """
            await Audit(audit, me, "identity.user", id, AuditAction.Update, "UserPasswordReset", null);
            """;

        Credential.IsMatch(StripCommentsAndStrings(sample)).Should().BeFalse(
            "recording THAT a password was reset is required; recording the password is not");
    }

    /// <summary>
    /// Drop comments and string literals before matching, keeping only interpolation holes.
    ///
    /// Without this the check fires on its own explanatory comments and on outcome names like
    /// "UserPasswordReset" — which are exactly what a good audit event SHOULD say. What matters is whether
    /// a credential-bearing VARIABLE is being passed, and that survives this stripping.
    /// </summary>
    /// <remarks>
    /// A string literal contributes NOTHING but its <c>{…}</c> holes. The earlier version stripped only
    /// literals containing no brace, which meant an INTERPOLATED message kept its prose — so
    /// <c>LogError(ex, "Password-reset email could not be sent for user {UserId}.", user.Id)</c> was reported
    /// as credential material on the strength of the word "Password" in an English sentence. A guard that
    /// fires on correct code is a guard that gets suppressed, and the suppression outlives the false
    /// positive. Holes are still kept, because <c>{user.PasswordHash}</c> inside a message IS the mistake.
    /// </remarks>
    private static string StripCommentsAndStrings(string source)
    {
        var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var noLineComments = Regex.Replace(noBlockComments, @"//[^\n]*", " ");

        return Regex.Replace(noLineComments, @"""(?:[^""\\]|\\.)*""", literal =>
            " " + string.Join(' ', Regex.Matches(literal.Value, @"\{([^{}]+)\}").Select(h => h.Groups[1].Value)) + " ");
    }

    private static IEnumerable<(string Absolute, string Relative)> ProductionFiles()
    {
        var root = RepoRoot();
        foreach (var dir in new[] { "libs", "services", "tools" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;

            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                    relative.Contains("/bin/", StringComparison.Ordinal) ||
                    relative.Contains("/Tests/", StringComparison.Ordinal)) continue;
                yield return (file, relative);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
