namespace Mersal.Auth;

/// <summary>
/// Decides whether an access token evidences multi-factor authentication, from its
/// acr / amr claims. Protected scopes require MFA (CLAUDE.md § Security).
/// </summary>
public static class MfaEvaluator
{
    public static bool IsSatisfied(string? acr, IReadOnlyList<string> amr)
    {
        if (amr is not null)
        {
            foreach (var a in amr)
            {
                foreach (var signal in MfaSignals.Amr)
                {
                    if (string.Equals(a, signal, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            // Two distinct authentication methods (e.g. pwd + otp) also implies MFA.
            var distinct = amr.Select(a => a.ToLowerInvariant()).Distinct().Count();
            if (distinct >= 2) return true;
        }

        if (!string.IsNullOrWhiteSpace(acr))
        {
            foreach (var signal in MfaSignals.Acr)
            {
                if (string.Equals(acr, signal, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
