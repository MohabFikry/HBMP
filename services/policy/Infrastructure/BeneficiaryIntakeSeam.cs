using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// The person a bulk intake row describes, keyed by the number on their card.
///
/// <para>Deliberately the whole person rather than an id: the operator building an intake file works from
/// cards and case papers and has never seen a <c>beneficiary_id</c>. Requiring one meant every intake had to
/// be preceded by a lookup pass that produced the very ids the file existed to create.</para>
/// </summary>
public sealed record BeneficiaryIntake(
    string CardNumber,
    string FirstName,
    string? MiddleName,
    string LastName,
    string Gender,
    string Nationality,
    string Phone,
    DateOnly? BirthDate,
    string? IndividualNo,
    string? CaseNo,
    string? Status,
    IReadOnlyList<(short Slot, string Value)> Notes,
    /// <summary>The coverage the row elects, recorded on the registration as an INTENT — the same shape the
    /// form produces. A row that leaves the member Pending is enrolled when they are approved, by the same
    /// consumer that handles the form path; without the intent stored here, approving an imported member
    /// would issue a card number and no coverage.</summary>
    Guid PlanId,
    Guid NetworkTierId,
    decimal ContributionPercent,
    Guid? BranchId);

/// <summary>What patient-service did with it, and who it now is.</summary>
public sealed record BeneficiaryIntakeResult(Guid BeneficiaryId, string Status, string? MemberNo, bool Created, bool Changed);

/// <summary>
/// Register-or-update a beneficiary by card number.
///
/// <para><b>This is what makes a re-upload safe.</b> The same card is the same person, so a corrected file
/// updates the record it created last time instead of creating a second one. Without it, the only way to fix
/// one bad row in a ten-thousand-row file would be to hand-edit the file down to that row — which is how a
/// second, subtly different copy of a member's record gets created.</para>
///
/// <para>patient-service owns beneficiaries, so it owns this decision. policy-service asks; it does not reach
/// into another service's table to decide what "the same person" means.</para>
/// </summary>
public interface IBeneficiaryIntake
{
    Task<BeneficiaryIntakeResult?> UpsertAsync(BeneficiaryIntake intake, string? bearerToken, CancellationToken ct = default);
}

public sealed class HttpBeneficiaryIntake(HttpClient http) : IBeneficiaryIntake
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BeneficiaryIntakeResult?> UpsertAsync(
        BeneficiaryIntake intake, string? bearerToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intake);

        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/beneficiaries/by-card")
        {
            Content = JsonContent.Create(new
            {
                intake.CardNumber,
                GivenName = intake.FirstName,
                intake.MiddleName,
                FamilyName = intake.LastName,
                Sex = intake.Gender,
                NationalityCode = intake.Nationality,
                intake.Phone,
                BirthDate = intake.BirthDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                intake.IndividualNo,
                intake.CaseNo,
                intake.Status,
                Notes = intake.Notes.Select(n => new { n.Slot, n.Value }),
                Enrolment = new
                {
                    intake.PlanId, intake.NetworkTierId, intake.ContributionPercent,
                    DefaultBranchId = intake.BranchId,
                },
            }, options: Json),
        };
        Authorize(req, bearerToken);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BeneficiaryProbeRefusedException((int)resp.StatusCode);
        // NOT fail-soft, for the same reason the status probe is not: an unreachable patient-service means we
        // cannot tell whether this card belongs to somebody already, and guessing creates the duplicate.
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<BeneficiaryIntakeResult>(Json, ct);
    }

    private static void Authorize(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
