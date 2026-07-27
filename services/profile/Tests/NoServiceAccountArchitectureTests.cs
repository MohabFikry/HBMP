using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mersal.Profile.Infrastructure;

namespace Mersal.Profile.Tests;

/// <summary>
/// The architecture test build prompt 20.1 asks for: <b>this service must have no way to authenticate as
/// itself.</b>
///
/// <para>Why a source scan rather than a unit test. The vulnerability is an OMISSION-shaped defect — nobody
/// writes "compose under a service account" on purpose; it arrives as one `AddClientCredentials` in a DI file,
/// added to fix a 401 in a staging environment, and every test still passes because every downstream call now
/// succeeds. The resulting profile is complete rather than correct, which is the one failure mode that does not
/// look like a failure. Only a rule that asserts the pattern holds EVERYWHERE, including in the file that does
/// not exist yet, can catch it.</para>
/// </summary>
public class NoServiceAccountArchitectureTests
{
    /// <summary>Every way a .NET service has of acquiring a token of its own.</summary>
    private static readonly string[] ForbiddenPatterns =
    [
        "client_credentials",
        "ClientCredentials",
        "ClientSecret",
        "client_secret",
        "AddClientAccessTokenHandler",   // IdentityModel's machine-to-machine handler
        "ClientCredentialsTokenRequest",
        "AcquireTokenForClient",         // MSAL's equivalent
        "ManagedIdentityCredential",
        "DefaultAzureCredential",
    ];

    [Fact]
    public void The_profile_service_has_no_client_credentials_path()
    {
        var offenders = new List<string>();

        foreach (var file in ServiceSourceFiles())
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var pattern in ForbiddenPatterns)
            {
                if (code.Contains(pattern, StringComparison.Ordinal))
                    offenders.Add($"{Relative(file)}: '{pattern}'");
            }
        }

        offenders.Should().BeEmpty(
            "profile-service composes under the CALLER'S token and must have no way to authenticate as itself " +
            "(design 39 §7.2). A privileged aggregator that fetches everything and then filters is the classic " +
            "aggregation vulnerability:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void No_outgoing_request_sets_an_Authorization_header_from_anywhere_but_the_caller()
    {
        // The narrower rule underneath the one above: exactly ONE line in this service writes an Authorization
        // header, and it copies CallerCredentials.Authorization. A second one is how a "just for the health
        // check" token becomes the composition path.
        var writers = new List<string>();

        foreach (var file in ServiceSourceFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @".*""Authorization"".*"))
            {
                var line = m.Value.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                writers.Add($"{Relative(file)}: {line}");
            }
        }

        writers.Should().ContainSingle(
            "exactly one place may set an outgoing Authorization header, and it must copy the caller's — " +
            "found:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, writers));
        writers[0].Should().Contain("caller.Authorization");
    }

    [Fact]
    public void The_profile_service_owns_no_data()
    {
        // Design 39 §7.4. A DbContext or a migration here would mean a second source of truth for the record a
        // clinician makes decisions from — and it would arrive innocently, as a cache.
        var root = ServiceRoot();
        Directory.EnumerateFiles(root, "*.sql", SearchOption.AllDirectories).Should().BeEmpty(
            "profile-service is pure composition and has no schema");

        var offenders = ServiceSourceFiles()
            .Where(f => StripComments(File.ReadAllText(f)).Contains("DbContext", StringComparison.Ordinal))
            .Select(Relative).ToList();
        offenders.Should().BeEmpty("profile-service must have no DbContext: {0}", string.Join(", ", offenders));
    }

    /// <summary>
    /// Scan the CODE, not the prose.
    ///
    /// <para>Without this, the comment explaining why a service account is absent trips the rule that checks
    /// service accounts are absent — and the usual fix for that is to delete the comment, which leaves the next
    /// reader with a rule whose reason has been erased to keep the rule quiet.</para>
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
    }

    [Fact]
    public async Task The_caller_bearer_is_what_actually_reaches_the_owning_service()
    {
        // The behavioural half of the rule: assert the header on the wire, not the shape of the code.
        using var body = JsonDocument.Parse("""{"memberships":[]}""");
        using var recorder = new CountingHttp(body);
        var http = recorder.AsCallerScopedHttp();

        using var doc = await http.GetAsync(
            "policy", "/api/v1/beneficiaries/x/administrative-360",
            new Domain.CallerCredentials("Bearer caller-token-abc", "branch-1", "corr-9"), default);

        recorder.AuthorizationHeaders.Should().ContainSingle().Which.Should().Be("Bearer caller-token-abc");
    }

    // ---------------------------------------------------------------- helpers

    private static string ServiceRoot() => Path.Combine(RepoRoot(), "services", "profile");

    private static IEnumerable<string> ServiceSourceFiles() =>
        Directory.EnumerateFiles(ServiceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string p) => Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HbmpPlatform.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
