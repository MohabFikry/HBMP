using System.Globalization;
using Mersal.Admin.Domain;
using Mersal.Admin.Infrastructure;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Validity;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Admin.Api;

/// <summary>
/// How long a document is good for, and how early its lapse is warned about (ADR-0035 §6).
/// </summary>
/// <remarks>
/// <para>
/// The deliberate sibling of <see cref="ValidityPolicyEndpoints"/>, on the same store, the same gate and the
/// same "every kind is answered whether or not a row exists" discipline. A second shape for the same idea is
/// the drift both of them exist to prevent.
/// </para>
/// <para>
/// <b>Held by clinical governance, not by the platform admins</b>, on the argument ADR-0035 rests on: a
/// refugee whose card lapsed is stopped at reception, and the case that produces lands on the supervisor's
/// desk. The person who absorbs the consequence sets the number.
/// </para>
/// </remarks>
public static class DocumentValidityEndpoints
{
    public static void MapDocumentValidity(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/document-validity", async (
            AdminDbContext db, IHbmpPrincipalAccessor me, CancellationToken ct) =>
        {
            var tenant = me.Principal?.TenantId;
            if (string.IsNullOrWhiteSpace(tenant))
            {
                return Results.Problem(statusCode: 401, title: "no-tenant",
                    detail: "The caller's token carries no tenant.", type: "urn:hbmp:no-tenant");
            }

            var keys = DocumentValidityPolicy.All
                .SelectMany(k => new[] { DocumentValidityPolicy.KeyFor(k), DocumentValidityPolicy.WarnKeyFor(k) })
                .ToArray();

            var rows = await db.SystemConfigs.AsNoTracking()
                .Where(c => c.TenantId == tenant && c.EffectiveTo == null && keys.Contains(c.Key))
                .ToListAsync(ct);

            // Every kind is answered whether or not a row exists. A caller must never have to decide what a
            // missing key means — that decision is DefaultDays / DefaultWarnDays and it is made once, here.
            var items = DocumentValidityPolicy.All.Select(kind =>
            {
                var daysRow = rows.FirstOrDefault(r => r.Key == DocumentValidityPolicy.KeyFor(kind));
                var warnRow = rows.FirstOrDefault(r => r.Key == DocumentValidityPolicy.WarnKeyFor(kind));
                return new DocumentValidityItem(
                    kind.ToString(),
                    DocumentValidityPolicy.KeyFor(kind),
                    DocumentValidityPolicy.DaysFrom(daysRow?.Value),
                    DocumentValidityPolicy.WarnDaysFrom(warnRow?.Value),
                    Configured: daysRow is not null,
                    WarnConfigured: warnRow is not null,
                    Identity: DocumentValidityPolicy.IdentityKinds.Contains(kind),
                    UpdatedAt: daysRow?.UpdatedAt ?? warnRow?.UpdatedAt);
            }).ToList();

            return Results.Ok(new DocumentValidityView(
                tenant, DocumentValidityPolicy.DefaultDays, DocumentValidityPolicy.MinDays,
                DocumentValidityPolicy.MaxDays, DocumentValidityPolicy.DefaultWarnDays, items));
        }).RequireAuthorization(HbmpPolicies.Scope("admin:read"))
        .Produces<DocumentValidityView>();

        app.MapPut("/api/v1/admin/document-validity", async (
            SetDocumentValidityRequest req, AdminGate gate, GovernanceService svc, CancellationToken ct) =>
        {
            // The same action as the prescription/order validity periods: this IS a validity period, on a
            // different artefact. A separate action would be a second answer to "who decides how long
            // something stays good for", and the two would drift apart on the first disagreement.
            var denied = await gate.CheckAsync(AdminPolicies.EditValidityPolicy, ct);
            if (denied is not null) return denied;

            var p = gate.Principal!;
            var scope = gate.BindTenant(req.Tenant);
            if (!scope.IsAllowed) return scope.ToProblem();
            var tenant = scope.Tenant!;

            if (!Enum.TryParse<DocumentKind>(req.Kind, ignoreCase: true, out var kind))
            {
                return ProblemResults.Unprocessable("unknown-document-kind",
                    $"'{req.Kind}' is not a kind of document that carries a validity period.");
            }

            // Refused HERE as well as by ConfigValidation so the supervisor reads the bound rather than a type
            // error — somebody who typed 36500 needs "3650 at most", not "invalid Whole".
            if (req.Days is { } days && !DocumentValidityPolicy.IsInRange(days))
            {
                return ProblemResults.Unprocessable("validity-out-of-range",
                    $"A document validity must be between {DocumentValidityPolicy.MinDays} and "
                    + $"{DocumentValidityPolicy.MaxDays} days.");
            }

            // An empty threshold list is refused rather than stored. "Warn at no point" would silence an
            // expiring credential entirely, and a supervisor who meant that should have to say it some other
            // way than by clearing a field.
            if (req.WarnDays is { } warn && warn.Count > 0 && !warn.Any(DocumentValidityPolicy.IsInRange))
            {
                return ProblemResults.Unprocessable("warn-days-out-of-range",
                    $"Warning thresholds must be between {DocumentValidityPolicy.MinDays} and "
                    + $"{DocumentValidityPolicy.MaxDays} days.");
            }

            if (req.Days is null && req.WarnDays is null)
                return ProblemResults.Invalid("nothing-to-set", "Supply a cadence, thresholds, or both.");

            var actor = AdminContracts.Actor(p);
            int? savedVersion = null;

            if (req.Days is { } d)
            {
                var r = await svc.SetConfigAsync(actor, tenant, DocumentValidityPolicy.KeyFor(kind),
                    ConfigValueType.Whole, d.ToString(CultureInfo.InvariantCulture), ct);
                if (!r.Ok) return ProblemResults.Unprocessable("config-invalid", r.Error);
                savedVersion = r.Config!.VersionNo;
            }

            if (req.WarnDays is { } w)
            {
                var r = await svc.SetConfigAsync(actor, tenant, DocumentValidityPolicy.WarnKeyFor(kind),
                    ConfigValueType.Text, DocumentValidityPolicy.WarnDaysToValue(w), ct);
                if (!r.Ok) return ProblemResults.Unprocessable("config-invalid", r.Error);
                savedVersion ??= r.Config!.VersionNo;
            }

            // Says plainly what it does NOT do. A supervisor who shortens a cadence and expects yesterday's
            // recorded documents to lapse tonight has to learn that from somewhere, and retroactive expiry
            // would strand a beneficiary whose papers were fine when they were checked.
            return Results.Ok(new
            {
                kind = kind.ToString(),
                days = req.Days,
                warnDays = req.WarnDays,
                appliesTo = "documents recorded from now on; anything already recorded keeps the expiry it carries",
                version = savedVersion,
            });
        }).RequireAuthorization(HbmpPolicies.Scope("admin:write"));
    }
}

/// <summary>
/// One document kind's policy, and whether anyone has actually chosen it.
/// </summary>
/// <param name="Configured">
/// False = nobody has set this and the value shown is the platform default. The distinction is surfaced,
/// because "365 because we chose 365" and "365 because nobody has looked" are different states and only one
/// of them is a decision.
/// </param>
/// <param name="Identity">
/// True for the documents whose lapse stops a BENEFICIARY being seen, as opposed to stopping a provider
/// practising. Two different consequences reached by two different paths, and the screen says which.
/// </param>
public sealed record DocumentValidityItem(
    string Kind, string Key, int Days, IReadOnlyList<int> WarnDays,
    bool Configured, bool WarnConfigured, bool Identity, DateTimeOffset? UpdatedAt);

public sealed record DocumentValidityView(
    string Tenant, int DefaultDays, int MinDays, int MaxDays,
    IReadOnlyList<int> DefaultWarnDays, IReadOnlyList<DocumentValidityItem> Items);

/// <summary>Set a cadence, thresholds, or both. Omitting one leaves it untouched rather than clearing it.</summary>
public sealed record SetDocumentValidityRequest(
    string? Tenant, string Kind, int? Days, IReadOnlyList<int>? WarnDays);
