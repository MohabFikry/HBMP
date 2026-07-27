namespace Mersal.Policy.Domain;

/// <summary>
/// Phase 20.3 — the rules around a beneficiary's identification photograph (design 39 §5).
///
/// <para>A photo materially helps at the front desk: it confirms the person in front of you is the member on
/// the card, and it makes card-sharing hard. For a refugee population it is also identity-sensitive,
/// biometric-adjacent data, held by an organisation those people did not choose and cannot easily leave. Both
/// things are true, which is why the answer is "yes, with conditions" rather than either "no photos" or a photo
/// field on the registration form.</para>
/// </summary>
public static class IdentityPhotoRules
{
    /// <summary>How long a signed thumbnail URL lives. Long enough to render a page, short enough that the URL
    /// in a browser history or a support ticket is already dead.</summary>
    public static readonly TimeSpan SignedUrlTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A photo may only be stored when a recorded consent covering photography is on file.
    ///
    /// <para><b>Refusal is permitted and must not block care.</b> That sentence is the whole design: consent
    /// that cannot be refused without consequence is not consent, and a beneficiary who declines simply gets an
    /// initials avatar. Nothing downstream branches on whether a photo exists.</para>
    /// </summary>
    public static bool ConsentSatisfied(IEnumerable<PolicyDocument> memberDocuments, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(memberDocuments);
        return memberDocuments.Any(d =>
            d.DocumentClass == DocumentClass.ConsentForm
            && d.Status == DocumentLinkStatus.Active
            && !d.IsExpired(today));
    }

    /// <summary>The refusal message. It names what is missing AND that care is unaffected, because an officer
    /// reading "rejected" on a registration screen needs to know whether they are now blocked.</summary>
    public const string ConsentMissing =
        "An identification photograph may only be stored once a consent form covering photography is on file " +
        "for this member. Recording the consent first is the only step needed — a member who declines keeps " +
        "full access to care, and the profile simply shows their initials.";
}
