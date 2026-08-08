using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mersal.ClinicalValidation;
using Mersal.Pharmacy.Domain;
using Mersal.Pharmacy.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Mersal.BeneficiaryLookup;

namespace Mersal.Pharmacy.Api;

internal static class BearerHeader
{
    public static void Apply(HttpRequestMessage req, string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return;
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..] : bearerToken;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

/// <summary>Validates a prescription-line drug id against masterdata (fail-closed on writes), caching positives.</summary>
public sealed class HttpDrugValidator(HttpClient http, IMemoryCache cache) : IDrugValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves the drug and returns its display name (null = not in master data). Fail-closed on
    /// 5xx/transport, as before: an unvalidated drug is never persisted.</summary>
    public async Task<string?> DrugNameAsync(Guid drugId, string? bearerToken, CancellationToken ct = default)
    {
        var key = $"drug-name:{drugId}";
        if (cache.TryGetValue<string>(key, out var cached) && cached is not null) return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/drugs/by-id/{drugId}");
        BearerHeader.Apply(req, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DrugDto>(Json, ct);
        if (string.IsNullOrWhiteSpace(body?.Name)) return null;

        // Trade name plus strength and form, because that is what identifies the box on the shelf. "Augmentin"
        // alone does not tell a pharmacist whether to reach for 375mg or 1g, and the dose field beside it is
        // the prescribed dose, not the product's.
        var label = string.Join(" ", new[] { body!.Name, body.Strength, body.Form }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        cache.Set(key, label, TimeSpan.FromMinutes(30));   // master data is immutable within a deployment
        return label;
    }

    /// <summary>30.x — the pack facts, from the SAME /drugs/by-id call the name comes from. No new endpoint
    /// and no second round trip: masterdata already returns the whole drug row.</summary>
    public async Task<DrugPack?> PackAsync(Guid drugId, string? bearerToken, CancellationToken ct = default)
    {
        var key = $"drug-pack:{drugId}";
        if (cache.TryGetValue<DrugPack>(key, out var cached) && cached is not null) return cached;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/drugs/by-id/{drugId}");
        BearerHeader.Apply(req, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DrugDto>(Json, ct);
        if (body is null) return null;

        // ABSENCE IS CARRIED THROUGH. A null is_pack_splittable is not "true": the allocation reports
        // NotChecked and names the missing field, because a silently wrong quantity is a dispensing error.
        var pack = new DrugPack(body.IsPackSplittable, body.PackSize, body.PrescribingUnit);
        cache.Set(key, pack, TimeSpan.FromMinutes(30));
        return pack;
    }

    private sealed record DrugDto(
        Guid DrugId, string? Name, string? Strength, string? Form,
        bool? IsPackSplittable, decimal? PackSize, string? PrescribingUnit);
}

/// <summary>
/// Prescribe-time screening, backed by the shared validation engine (phase 26.3).
/// </summary>
/// <remarks>
/// <para>
/// This replaces an implementation that caught every <c>HttpRequestException</c> and returned no alerts, and
/// treated every non-2xx response the same way through a bare <c>if (resp.IsSuccessStatusCode)</c>. There
/// were six such paths across three calls. The effect was that an outage — or, after 26.1 scoped masterdata,
/// a token missing <c>masterdata:read</c> — rendered to the prescriber as a clean bill of health. Doc 43 §1
/// calls that the single most dangerous line in the prescribing path.
/// </para>
/// <para>
/// Every check that could not run now surfaces as an <see cref="AlertKind.Unavailable"/> alert. Screening
/// remains advisory and non-blocking, which is doc 43 D1's position — the prescriber may proceed past any
/// clinical warning with a recorded reason — but it can no longer stay silent about not having asked.
/// </para>
/// </remarks>
public sealed class ValidatorBackedPrescribingScreener(
    IClinicalValidationPorts ports, TimeProvider clock) : IPrescribingScreener
{
    public async Task<AlertScreening> ScreenAsync(
        Guid beneficiaryId, IReadOnlyList<Guid> drugIds, string? bearerToken, CancellationToken ct = default)
    {
        var screening = new AlertScreening();

        var lines = drugIds
            .Select(id => new PrescriptionLineInput(Guid.NewGuid(), id, id.ToString()))
            .ToList();

        // No encounter and so no diagnoses on this legacy path: it screens a DRUG SET, not a prescription
        // in context. Passing no encounter id and no client list yields an empty, client-supplied diagnosis
        // context — which the indication check reports as "no diagnosis recorded", never as Ok.
        var snapshot = await ports.FetchAsync(
            beneficiaryId, drugIds, encounterId: null, clientDiagnoses: null, bearerToken, ct);

        // No active-medication list either. It produces NotChecked findings, which are reported rather than
        // assumed away.
        var request = new ValidationRequest(Guid.Empty, lines, []);
        var result = PrescriptionValidator.Validate(request, snapshot, clock.GetUtcNow());

        foreach (var finding in result.Findings)
        {
            switch (finding)
            {
                case { State: CheckState.Unavailable }:
                    screening.AddUnavailable($"{finding.Kind}: {finding.MessageEn}");
                    break;
                case { Kind: CheckKind.Interaction, State: CheckState.Warning }:
                    screening.AddInteraction(finding.Severity?.ToString() ?? "Unknown", finding.MessageEn);
                    break;
                case { Kind: CheckKind.Allergy, State: CheckState.Warning }:
                    screening.AddAllergy(finding.MessageEn);
                    break;
                default:
                    break;
            }
        }

        return screening;
    }
}

/// <summary>Formulary/PBM stand-in (phase 6.3): today it reads masterdata's policy-approved alternatives for a drug.
/// A clearly-marked, swappable interface (<see cref="IFormularyService"/>) so a future external PBM can replace it
/// without touching the dispensing rule. Fail-safe: a lookup failure yields no alternatives → substitution is blocked
/// and routed to approvals (never dispense off-list).</summary>
public sealed class MasterDataFormularyService(IHttpClientFactory factory) : IFormularyService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Guid>> ApprovedAlternativesAsync(Guid drugId, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            var masterdata = factory.CreateClient("masterdata");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/drugs/by-id/{drugId}/alternatives");
            BearerHeader.Apply(req, bearerToken);
            using var resp = await masterdata.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];
            var body = await resp.Content.ReadFromJsonAsync<AlternativesDto>(Json, ct);
            return body?.Alternatives ?? [];
        }
        catch (HttpRequestException) { return []; }   // fail-safe: no off-list substitution on transport failure
    }

    private sealed record AlternativesDto(Guid[]? Alternatives);
}

// HttpBeneficiaryResolver moved to libs/beneficiary-lookup (27.8) — see the note in
// PharmacyPersistence.cs.

/// <summary>Treating-relationship check via emr-service (token forwarded, boolean only). Fails closed.</summary>
public sealed class HttpTreatingRelationshipClient(HttpClient http) : ITreatingRelationshipClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> TreatsAsync(Guid beneficiaryId, string? bearerToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/treating-relationship?beneficiaryId={beneficiaryId}");
        BearerHeader.Apply(req, bearerToken);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadFromJsonAsync<TreatsDto>(Json, ct);
        return body?.Treats ?? false;
    }

    private sealed record TreatsDto(bool Treats);
}
