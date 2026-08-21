namespace Mersal.Admin.Api;

/// <summary>
/// The short, non-identifying reference a governance register shows in place of a person.
/// </summary>
/// <remarks>
/// <para>The break-glass dashboard is a register of who reached PHI in an emergency. Printing a name beside
/// each row turns it into a directory of who touched what, which is the disclosure the register exists to
/// deter — so the rows carry a token instead, and the screen says so in as many words.</para>
///
/// <para><b>Why this is a class and not a string interpolation in the SPA.</b> It was the interpolation:
/// admin-service sent <c>RequesterUserId</c> whole and <c>HttpApiClient</c> rendered
/// <c>`•••${id.replace(/-/g, "").slice(-4)}`</c>. The rule held for anybody looking at the table and for
/// nobody looking at the response, which is the wrong half — design 18 §2 puts minimum-necessary projection
/// on the server precisely because the client is not where a disclosure decision can be enforced. Moving it
/// here changes nothing on screen and everything on the wire.</para>
///
/// <para><b>What this is not.</b> It is a truncation, not a pseudonym: it is derived from the id, it is not
/// collision-free across a large tenant, and it is not meant to survive an attacker who already holds the
/// directory. It is the same four characters the screen has always shown, produced where the full value can
/// be withheld. A stable keyed pseudonym is a different design with a key-rotation story attached; if the
/// register ever needs one, this is the single place it goes.</para>
/// </remarks>
public static class GovernanceToken
{
    /// <summary>The bullet prefix that marks a value as deliberately shortened rather than truncated by accident.</summary>
    private const string Prefix = "•••";

    /// <summary>The token for a subject that is present but unreadable — the prefix with nothing behind it.
    /// Distinct from <c>null</c>, which means there is no subject at all.</summary>
    public const string Withheld = Prefix;

    /// <summary>Tokenise a subject id. Null in, null out — an unapproved grant has no approver, and inventing
    /// a token for one would read as somebody having approved it.</summary>
    public static string? Of(string? subjectId)
    {
        if (string.IsNullOrWhiteSpace(subjectId)) return null;
        var compact = subjectId.Replace("-", string.Empty, StringComparison.Ordinal);
        var tail = compact.Length <= 4 ? compact : compact[^4..];
        return Prefix + tail;
    }
}
