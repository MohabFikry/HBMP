using System.Text.Json;
using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Infrastructure;

// The four sections derived from policy-service's administrative-360, plus the member timeline. Design 39 §2:
// the phase-19 administrative 360 BECOMES header + coverage + documents + notes + timeline. It is re-pointed,
// not duplicated — these providers read that one response and shape it, and policy-service remains the only
// place that decides what an administrative caller may read.

/// <summary>Section 1 — identity strip. The photo URL is offered only to the design-39 §5 allow-list; for
/// everyone else the field is absent (stripped by <see cref="SectionProjection"/>, not rendered-and-hidden).</summary>
public sealed class HeaderSectionProvider(AdministrativeSource source) : ISectionProvider
{
    public string Key => ProfileSections.Header;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var beneficiary = doc.Prop("beneficiary");
        var membership = doc.Array("memberships").FirstOrDefault();

        var status = membership.ValueKind == JsonValueKind.Object
            ? membership.Str("status") ?? "Pending"
            : beneficiary?.Str("status") ?? "Pending";

        // patient-service emits the name as givenName + familyName (BeneficiaryReadGuard.Fields), classified
        // Identity so every role that may read the record gets it. This looked only for displayName / fullNameEn
        // / name — none of which patient-service has ever sent — so EVERY profile header rendered
        // "(name unavailable)". Worst where it matters most: the call-centre agent completes verify-before-
        // disclose specifically so they can greet the caller by name, and then could not see it.
        var composed = string.Join(" ", new[] { beneficiary?.Str("givenName"), beneficiary?.Str("familyName") }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var displayName = beneficiary?.Str("displayName")
            ?? beneficiary?.Str("fullNameEn")
            ?? beneficiary?.Str("name")
            ?? (composed.Length > 0 ? composed : null)
            // Still explicit rather than blank: a missing name is a real state (an incomplete registration) and
            // must not read as an empty label.
            ?? "(name unavailable)";

        return new HeaderSection(
            request.BeneficiaryId,
            membership.ValueKind == JsonValueKind.Object ? membership.Str("memberNo") : null,
            displayName,
            beneficiary?.Str("fullNameAr"),
            beneficiary?.Str("ageBand"),
            beneficiary?.Str("sex"),
            status,
            StatusCue.For(status),
            beneficiary?.Str("branchName"),
            beneficiary?.Str("preferredLanguage"),
            // Same mismatch: patient-service sends contacts[] (class Contact), not a flat primaryPhone, so the
            // header's contact line was always empty for callers who ARE allowed the phone number.
            PrimaryPhone(beneficiary) is { } phone
                ? new ContactSummary(phone, beneficiary?.Str("preferredChannel"))
                : null,
            // A relative path to this service's own gated endpoint, never a blob URL. The bytes are behind a
            // second authorization check and a short-TTL signature (design 39 §5).
            $"/api/v1/patients/{request.BeneficiaryId}/photo");
    }

    /// <summary>The primary phone out of patient-service's contacts[], preferring the one flagged primary and
    /// falling back to the first phone. Absent when the caller's field projection withheld contacts entirely,
    /// which is a different thing from the member having no phone — hence null, not an empty string.</summary>
    private static string? PrimaryPhone(JsonElement? beneficiary)
    {
        if (beneficiary?.Array("contacts") is not { } contacts) return null;
        var phones = contacts
            .Where(c => c.ValueKind == JsonValueKind.Object
                        && string.Equals(c.Str("type"), "Phone", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (phones.Count == 0) return null;
        var primary = phones.FirstOrDefault(c => c.TryGetProperty("isPrimary", out var f) && f.ValueKind == JsonValueKind.True);
        return (primary.ValueKind == JsonValueKind.Object ? primary : phones[0]).Str("value");
    }
}

/// <summary>Section 3 — payer, plan, effective window, per-category limits with consumed/remaining and the
/// per-tier cost share (design 38).</summary>
public sealed class CoverageSectionProvider(AdministrativeSource source, CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Coverage;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var membership = doc.Array("memberships").FirstOrDefault();
        if (membership.ValueKind != JsonValueKind.Object) return null;

        var categories = new List<CoverageLimitLine>();
        if (membership.Uuid("enrollmentId") is { } enrollmentId)
        {
            // The limits live behind the enrollment's coverage-detail endpoint, which is where consumed/remaining
            // and the tier cost-share are computed. Recomputing them here would be a second answer to "how much
            // is left", and the two would disagree the first time a limit reset ran.
            using var detail = await http.GetAsync(
                CoverageSource, $"/api/v1/enrollments/{enrollmentId}/coverage-details", request.Caller, ct);
            if (detail is not null)
            {
                foreach (var line in detail.RootElement.Array("categories"))
                {
                    categories.Add(new CoverageLimitLine(
                        line.Str("category") ?? line.Str("benefitCategory") ?? "(unknown)",
                        line.Dec("annualLimit"), line.Dec("consumed"), line.Dec("remaining"),
                        line.Dec("costSharePercent"), line.Str("costShareTier")));
                }
            }
        }

        return new CoverageSection(
            membership.Str("payerName"),
            membership.Str("policyNo"),
            membership.Str("planLabel"),
            membership.Num("planVersionNo"),
            membership.Day("effectiveFrom"),
            membership.Day("effectiveTo"),
            membership.Str("waitingPeriodState"),
            categories);
    }

    private const string CoverageSource = AdministrativeSource.ClientName;
}

/// <summary>Section 10 — classified documents (design 38 §5b). Metadata always; the BYTES are a separate,
/// separately-audited authority, which is why each row carries <c>mayDownload</c> rather than a link.</summary>
public sealed class DocumentsSectionProvider(AdministrativeSource source) : ISectionProvider
{
    public string Key => ProfileSections.Documents;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var rows = doc.Array("documents")
            .Where(d => !string.Equals(d.Str("documentClass"), IdentityPhoto, StringComparison.Ordinal))
            .Select(d => new DocumentRow(
                d.Uuid("linkId") ?? Guid.Empty,
                d.Str("documentClass") ?? "Other",
                d.Str("visibilityClass") ?? "Administrative",
                d.Str("title") ?? "(untitled)",
                d.Day("documentDate"),
                d.Moment("uploadedAt") ?? default,
                d.Str("status") ?? "Active",
                d.Bool("contentAccessible")))
            .ToList();

        return new DocumentsSection(rows);
    }

    /// <summary>The identity photo is a document, but it is NOT listed here. It has its own endpoint, its own
    /// narrower allow-list and its own audit event; letting it appear in the general document list would hand it
    /// to every role entitled to see that a consent form exists (design 39 §5).</summary>
    public const string IdentityPhoto = "IdentityPhoto";
}

/// <summary>Section 11 — policy/member notes, class-projected. A note whose class the caller lacks arrives with
/// its body withheld and its existence intact: 19.3's rule, passed through unchanged.</summary>
public sealed class NotesSectionProvider(AdministrativeSource source) : ISectionProvider
{
    public string Key => ProfileSections.Notes;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var rows = doc.Array("notes")
            .Select(n => new NoteRow(
                n.Uuid("noteId") ?? Guid.Empty,
                n.Str("noteType") ?? "General",
                n.Str("visibilityClass") ?? "Administrative",
                n.Str("body"),
                n.Str("authoredByDisplay") ?? "(unknown)",
                n.Moment("authoredAt") ?? default,
                n.Bool("bodyWithheld"),
                n.Bool("pinned")))
            .ToList();

        return new NotesSection(rows);
    }
}

/// <summary>Section 14 — the unified change/access history (design 38 §5c), keyed on the member's enrollment.</summary>
public sealed class TimelineSectionProvider(AdministrativeSource source, CallerScopedHttp http) : ISectionProvider
{
    public string Key => ProfileSections.Timeline;

    public async Task<object?> FetchAsync(SectionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = await source.GetAsync(request.BeneficiaryId, request.Caller, ct);
        if (root is not { } doc) return null;

        var enrollmentId = doc.Array("memberships").FirstOrDefault().Uuid("enrollmentId");
        if (enrollmentId is null) return null;

        using var timeline = await http.GetAsync(
            AdministrativeSource.ClientName, $"/api/v1/enrollments/{enrollmentId}/timeline?pageSize=100",
            request.Caller, ct);
        if (timeline is null) return null;

        var rows = timeline.RootElement.Array("entries")
            .Select(e => new TimelineRow(
                e.Moment("occurredAt") ?? default,
                e.Str("eventType") ?? "(unknown)",
                e.Str("visibilityClass") ?? "Administrative",
                e.Str("actorDisplay") ?? e.Str("actorUsername"),
                e.Str("summary"),
                e.Str("sourceService") ?? "policy"))
            .ToList();

        return new TimelineSection(rows);
    }
}
