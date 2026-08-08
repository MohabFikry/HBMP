namespace Mersal.Policy.Domain;

/// <summary>
/// Who is covered together.
///
/// <para><b>The household is rooted on the principal.</b> An enrolment either IS a principal
/// (<c>principal_enrollment_id</c> null) or points at one, so "the family" is the principal plus everyone
/// pointing at it. Rooting the traversal is what makes it symmetric: asked from the father you get the
/// children, and asked from a child you get the father AND the other children.</para>
///
/// <para>That symmetry is the bug this type exists to prevent. The 360's family section walked the graph one
/// hop from the rows it already had — from a dependent that reaches the principal and stops, so a child's
/// record listed their father and none of their siblings. The relationship is not one hop from the person
/// asking; it is one hop from the ROOT.</para>
///
/// <para>This is the COVERED family, a membership fact, and deliberately not patient-service's household.
/// The two disagree the moment a relative lives in the flat and is not enrolled, and on this surface the
/// question being asked is always "who else does this cover reach".</para>
/// </summary>
public static class Household
{
    /// <summary>The enrolment every member of this household hangs from — the principal's, which for a
    /// principal is its own.</summary>
    public static Guid RootOf(Guid enrollmentId, Guid? principalEnrollmentId) =>
        principalEnrollmentId ?? enrollmentId;

    /// <summary>The distinct roots for a set of enrolments. A person can hold more than one membership (two
    /// policies, two households), and each is rooted separately.</summary>
    public static IReadOnlyList<Guid> RootsOf(IEnumerable<(Guid EnrollmentId, Guid? PrincipalEnrollmentId)> enrollments)
    {
        ArgumentNullException.ThrowIfNull(enrollments);
        return [.. enrollments.Select(e => RootOf(e.EnrollmentId, e.PrincipalEnrollmentId)).Distinct()];
    }

    /// <summary>
    /// Reading order for a family list: the principal first, then spouse, children, other dependants, and
    /// within a relationship by member number.
    ///
    /// <para>Not alphabetical by name. A family list is read to find the person who is standing at the desk,
    /// and a household reads in a shape people already hold in their heads — the principal is who the cover
    /// belongs to, and it is the row every other row is defined against.</para>
    /// </summary>
    public static int SortKey(bool isPrincipal, Relationship relationship) =>
        isPrincipal ? 0 : relationship switch
        {
            Relationship.Principal => 1,   // a principal of ANOTHER household, enrolled here as well
            Relationship.Spouse => 2,
            Relationship.Child => 3,
            _ => 4,
        };
}
