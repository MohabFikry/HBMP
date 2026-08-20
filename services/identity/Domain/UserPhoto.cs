namespace Mersal.Identity.Domain;

/// <summary>
/// A staff member's avatar — 28.15.
///
/// <para><b>Display only.</b> Nothing authorizes on it, nothing branches on its presence, and it is not in
/// the token. It sits beside <see cref="ApplicationUser.DisplayName"/> in kind: a caption for a person, shown
/// to the colleagues who work with them.</para>
///
/// <para>A BENEFICIARY's photograph is a different thing and is deliberately not stored like this — it is
/// biometric-adjacent data about a refugee, held behind profile-service with a narrower allow-list, a
/// short-TTL signed URL and an audit event on every read (design 39 §5). The distinction is worth keeping
/// clear, because the two would otherwise look like the same feature.</para>
/// </summary>
public sealed class UserPhoto
{
    public Guid UserId { get; set; }

    /// <summary>One of the three the migration's CHECK allows. Verified against the magic bytes on write —
    /// a declared content type is a claim by whoever uploaded it.</summary>
    public string ContentType { get; set; } = string.Empty;

    public byte[] Bytes { get; set; } = [];

    /// <summary>Stored rather than derived from <see cref="Bytes"/>, so the size bound is a database
    /// constraint rather than a promise the writing code makes.</summary>
    public int ByteSize { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The subject who set it, which may not be the subject it depicts: an administrator can set a
    /// photo for somebody else, and the person in the picture is entitled to know who chose it.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}
