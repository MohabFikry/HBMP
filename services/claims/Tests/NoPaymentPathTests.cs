using FluentAssertions;

namespace Mersal.Claims.Tests;

/// <summary>The platform NEVER moves money (36 §8). This scans the claims-service production source (Domain +
/// Infrastructure + Api — not the tests) for any payment-execution identifier: there must be no code path that
/// initiates a payment or transfer. The settlement advice is the hand-off; Finance/treasury pays externally.</summary>
public class NoPaymentPathTests
{
    // camelCase/PascalCase identifiers that would only appear in payment-EXECUTION code — deliberately distinct from
    // the prose ("no payout endpoint", "initiates no transfer") so a legitimate comment never trips this.
    private static readonly string[] Forbidden =
    [
        "ExecutePayment", "InitiatePayment", "SendPayment", "MakePayment", "TransferFunds", "BankTransfer",
        "MakePayout", "InitiateTransfer", "PaymentGateway", "PaymentRail", "DisburseFunds", "IssueTransfer",
    ];

    [Fact]
    public void No_claims_source_file_contains_a_payment_execution_identifier()
    {
        var root = ClaimsSourceRoot();
        var files = new[] { "Domain", "Infrastructure", "Api" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        files.Should().NotBeEmpty("the claims production source must be discoverable");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in Forbidden)
                text.Should().NotContain(token, $"{Path.GetFileName(file)} must contain no payment-execution path ({token})");
        }
    }

    private static string ClaimsSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "services", "claims");
            if (Directory.Exists(candidate)) return candidate;
            // also handle running from within services/claims/Tests/bin/...
            if (dir.Name == "claims" && Directory.Exists(Path.Combine(dir.FullName, "Domain"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate services/claims from the test base directory");
    }
}
