using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Validity;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>
/// How long a prescription or an investigation order stays actionable — read by every service that writes
/// one, set by the Medical Director who supervises the approval queue.
/// </summary>
/// <remarks>
/// <para>
/// The values are ordinary <see cref="SystemConfig"/> rows: effective-dated, versioned, type-validated and
/// audited by machinery that already existed. What is new here is a pair of endpoints with the right
/// audiences on each end, because the general config surface has the wrong one at both:
/// </para>
/// <list type="bullet">
///   <item><b>Read</b> is authenticated-only. The general <c>GET /system-config</c> requires
///   <c>admin:read</c> and admin-ness, which no doctor, pharmacist or technician has — and pharmacy and
///   orders must resolve this on the write path while holding the CLINICIAN's token. "Prescriptions are
///   valid for 10 days" is not confidential; it is printed on the screen the patient is looking at. The
///   endpoint discloses four integers and nothing else, and reads no patient data to produce them.</item>
///   <item><b>Write</b> is <see cref="AdminPolicies.EditValidityPolicy"/> — Medical Director or Super Admin.
///   The general config write is held by <c>org_admin</c>/<c>super_admin</c>, which would have put a
///   clinical safety parameter in the hands of the people who administer accounts.</item>
/// </list>
/// </remarks>
public static class ValidityPolicyEndpoints
{
    public static void MapValidityPolicy(this WebApplication app)
    {
        // Authenticated, no scope. See the class remarks: this is the reference read that pharmacy and
        // orders make while holding a prescriber's or a technician's token.
        app.MapGet("/api/v1/admin/validity-policy", async (
            AdminDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrWhiteSpace(tenant))
                return Results.Problem(statusCode: 401, title: "no-tenant",
                    detail: "The caller's token carries no tenant.", type: "urn:hbmp:no-tenant");

            var keys = ValidityPolicy.All.Select(ValidityPolicy.KeyFor).ToArray();
            var rows = await db.SystemConfigs.AsNoTracking()
                .Where(c => c.TenantId == tenant && c.EffectiveTo == null && keys.Contains(c.Key))
                .ToListAsync(ct);

            // Every artefact is answered, whether or not a row exists. A caller must never have to decide
            // what a missing key means — that decision is ValidityPolicy.DefaultDays and it is made once.
            var items = ValidityPolicy.All.Select(a =>
            {
                var key = ValidityPolicy.KeyFor(a);
                var row = rows.FirstOrDefault(r => r.Key == key);
                return new ValidityPolicyItem(
                    a.ToString(), key, ValidityPolicy.DaysFrom(row?.Value), row is not null, row?.UpdatedAt);
            }).ToList();

            return Results.Ok(new ValidityPolicyView(tenant, ValidityPolicy.DefaultDays,
                ValidityPolicy.MinDays, ValidityPolicy.MaxDays, items));
        }).RequireAuthorization()
        .Produces<ValidityPolicyView>();

        app.MapPut("/api/v1/admin/validity-policy", async (
            SetValidityPolicyRequest req, AdminGate gate, GovernanceService svc, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(AdminPolicies.EditValidityPolicy, ct);
            if (denied is not null) return denied;
            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            if (!Enum.TryParse<ValidityArtefact>(req.Artefact, ignoreCase: true, out var artefact))
                return ProblemResults.Unprocessable("unknown-artefact",
                    $"'{req.Artefact}' is not a thing that carries a validity period.");

            // Refused HERE as well as by ConfigValidation, so the caller is told the clinical bound rather
            // than a type error. A supervisor who typed 3650 needs to read "365 at most", not "invalid Whole".
            if (!ValidityPolicy.IsInRange(req.Days))
                return ProblemResults.Unprocessable("validity-out-of-range",
                    $"A validity period must be between {ValidityPolicy.MinDays} and {ValidityPolicy.MaxDays} days.");

            var result = await svc.SetConfigAsync(AdminContracts.Actor(p), tenant,
                ValidityPolicy.KeyFor(artefact), ConfigValueType.Whole,
                req.Days.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

            if (!result.Ok) return ProblemResults.Unprocessable("config-invalid", result.Error);

            // The response says plainly that this does not touch anything already written. A supervisor who
            // shortens the window and expects yesterday's prescriptions to expire tonight has to learn that
            // from somewhere, and the alternative — retroactive expiry — would strand patients holding a
            // valid prescription they were told to come back with.
            return Results.Ok(new ValidityPolicyChangeView(
            artefact.ToString(), req.Days,
            "prescriptions and orders written from now on; existing ones keep the expiry they were issued with",
            result.Config!.VersionNo, result.Config.EffectiveFrom));
        }).RequireAuthorization(HbmpPolicies.Scope("admin:write"))
        .Produces<ValidityPolicyChangeView>();
    }
}

/// <summary>One artefact's validity period, and whether anyone has actually chosen it.</summary>
/// <param name="Configured">False = nobody has set this; <paramref name="Days"/> is the platform default.
/// The distinction is shown in the supervisor UI, because "10 because we chose 10" and "10 because nobody
/// has looked at this" are different states and only one of them is a decision.</param>
public sealed record ValidityPolicyItem(
    string Artefact, string Key, int Days, bool Configured, DateTimeOffset? UpdatedAt);

public sealed record ValidityPolicyView(
    string Tenant, int DefaultDays, int MinDays, int MaxDays, IReadOnlyList<ValidityPolicyItem> Items);

public sealed record SetValidityPolicyRequest(string? Tenant, string Artefact, int Days);
