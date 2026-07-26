using System.Text.Json.Nodes;
using Mersal.Interop.Domain.Fhir;
using Mersal.Interop.Domain.Model;

namespace Mersal.Interop.Domain.Mapping;

/// <summary>
/// Pure HBMP-internal → FHIR R4 resource mappers (17-api-specifications §12). Each returns a spec-shaped JSON
/// object; there is no I/O and no authorization here — the caller has already passed the min-necessary gate and
/// read the source under the caller's bearer token. Deterministic + fully unit-testable.
/// </summary>
public static class FhirMappers
{
    public static JsonObject Patient(BeneficiarySource b)
    {
        ArgumentNullException.ThrowIfNull(b);
        var p = Fhir.Fhir.Resource("Patient", b.Id);

        if (b.Identifiers.Count > 0)
        {
            var ids = new JsonArray();
            foreach (var i in b.Identifiers)
                ids.Add(Fhir.Fhir.Identifier(Fhir.Fhir.IdentifierSystems.For(i.Type), i.Value));
            p["identifier"] = ids;
        }

        if (!string.IsNullOrWhiteSpace(b.FamilyName) || !string.IsNullOrWhiteSpace(b.GivenName))
        {
            var name = new JsonObject { ["use"] = "official" };
            if (!string.IsNullOrWhiteSpace(b.FamilyName)) name["family"] = b.FamilyName;
            if (!string.IsNullOrWhiteSpace(b.GivenName)) name["given"] = new JsonArray(b.GivenName);
            p["name"] = new JsonArray(name);
        }

        if (b.BirthDate is { } bd) p["birthDate"] = bd.ToString("yyyy-MM-dd");
        if (!string.IsNullOrWhiteSpace(b.Gender)) p["gender"] = NormalizeGender(b.Gender);

        if (b.Telecoms.Count > 0)
        {
            var tel = new JsonArray();
            foreach (var t in b.Telecoms)
            {
                var e = new JsonObject { ["system"] = t.System, ["value"] = t.Value };
                if (!string.IsNullOrWhiteSpace(t.Use)) e["use"] = t.Use;
                tel.Add(e);
            }
            p["telecom"] = tel;
        }

        if (b.Addresses.Count > 0)
        {
            var addrs = new JsonArray();
            foreach (var a in b.Addresses)
            {
                var e = new JsonObject();
                if (!string.IsNullOrWhiteSpace(a.Line)) e["line"] = new JsonArray(a.Line);
                if (!string.IsNullOrWhiteSpace(a.City)) e["city"] = a.City;
                if (!string.IsNullOrWhiteSpace(a.District)) e["district"] = a.District;
                if (!string.IsNullOrWhiteSpace(a.Country)) e["country"] = a.Country;
                addrs.Add(e);
            }
            p["address"] = addrs;
        }

        return p;
    }

    public static JsonObject Coverage(CoverageSource c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var cov = Fhir.Fhir.Resource("Coverage", c.Id);
        cov["status"] = string.Equals(c.Status, "Active", StringComparison.OrdinalIgnoreCase) ? "active" : "cancelled";
        cov["beneficiary"] = Fhir.Fhir.Reference("Patient", c.BeneficiaryId);
        if (!string.IsNullOrWhiteSpace(c.PayorName))
            cov["payor"] = new JsonArray(new JsonObject { ["display"] = c.PayorName });

        if (!string.IsNullOrWhiteSpace(c.ClassValue))
        {
            cov["class"] = new JsonArray(new JsonObject
            {
                ["type"] = Fhir.Fhir.CodeableConcept(
                    "http://terminology.hl7.org/CodeSystem/coverage-class", c.ClassCategory ?? "plan"),
                ["value"] = c.ClassValue,
            });
        }

        if (c.CostToBeneficiary.Count > 0)
        {
            var costs = new JsonArray();
            foreach (var cost in c.CostToBeneficiary)
                costs.Add(new JsonObject
                {
                    ["type"] = Fhir.Fhir.CodeableConcept(
                        "http://terminology.hl7.org/CodeSystem/coverage-copay-type", cost.Type),
                    ["valueMoney"] = new JsonObject { ["value"] = cost.Amount, ["currency"] = cost.Currency },
                });
            cov["costToBeneficiary"] = costs;
        }

        // Coverage limits carry as an extension (FHIR core Coverage has no annual-limit element).
        if (c.Limits.Count > 0)
        {
            var exts = new JsonArray();
            foreach (var l in c.Limits)
            {
                var parts = new JsonArray(new JsonObject { ["url"] = "category", ["valueString"] = l.Category });
                if (l.Limit is { } lim) parts.Add(new JsonObject { ["url"] = "annualLimit", ["valueDecimal"] = lim });
                if (l.Remaining is { } rem) parts.Add(new JsonObject { ["url"] = "remaining", ["valueDecimal"] = rem });
                exts.Add(new JsonObject { ["url"] = "https://mersal.org/fhir/StructureDefinition/coverage-limit", ["extension"] = parts });
            }
            cov["extension"] = exts;
        }

        return cov;
    }

    public static JsonObject ServiceRequest(ServiceRequestSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sr = Fhir.Fhir.Resource("ServiceRequest", s.Id);
        sr["status"] = StatusMaps.ServiceRequest(s.HbmpStatus);
        sr["intent"] = "order";
        sr["subject"] = Fhir.Fhir.Reference("Patient", s.BeneficiaryId);
        if (s.Code is { } code) sr["code"] = Fhir.Fhir.CodeableConcept(code.System, code.Code, code.Display);
        if (s.Quantity is { } q) sr["quantityQuantity"] = Fhir.Fhir.Quantity(q, s.QuantityUnit);
        if (!string.IsNullOrWhiteSpace(s.RequesterId)) sr["requester"] = Fhir.Fhir.Reference("Practitioner", s.RequesterId);
        if (string.Equals(s.Intent, "referral", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Category, "referral", StringComparison.OrdinalIgnoreCase))
        {
            sr["category"] = new JsonArray(Fhir.Fhir.CodeableConcept("http://snomed.info/sct", "306206005", "Referral to service"));
            if (!string.IsNullOrWhiteSpace(s.PerformerId)) sr["performer"] = new JsonArray(Fhir.Fhir.Reference("Organization", s.PerformerId));
        }
        else if (!string.IsNullOrWhiteSpace(s.Category))
        {
            sr["category"] = new JsonArray(Fhir.Fhir.CodeableConcept(Fhir.Fhir.Systems.Loinc, s.Category!));
        }
        return sr;
    }

    public static JsonObject MedicationRequest(MedicationRequestSource m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var mr = Fhir.Fhir.Resource("MedicationRequest", m.Id);
        mr["status"] = StatusMaps.MedicationRequest(m.HbmpStatus);
        mr["intent"] = "order";
        mr["subject"] = Fhir.Fhir.Reference("Patient", m.BeneficiaryId);
        if (m.Medication is { } med)
            mr["medicationCodeableConcept"] = Fhir.Fhir.CodeableConcept(med.System, med.Code, med.Display);
        if (!string.IsNullOrWhiteSpace(m.DosageText))
            mr["dosageInstruction"] = new JsonArray(new JsonObject { ["text"] = m.DosageText });
        if (m.DispenseQuantity is { } dq)
            mr["dispenseRequest"] = new JsonObject { ["quantity"] = Fhir.Fhir.Quantity(dq, m.DispenseUnit) };
        if (!string.IsNullOrWhiteSpace(m.RequesterId)) mr["requester"] = Fhir.Fhir.Reference("Practitioner", m.RequesterId);
        return mr;
    }

    public static JsonObject DiagnosticReport(DiagnosticReportSource d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var dr = Fhir.Fhir.Resource("DiagnosticReport", d.Id);
        dr["status"] = StatusMaps.DiagnosticReport(d.HbmpStatus);
        dr["subject"] = Fhir.Fhir.Reference("Patient", d.BeneficiaryId);
        if (d.Code is { } code) dr["code"] = Fhir.Fhir.CodeableConcept(code.System, code.Code, code.Display);
        if (!string.IsNullOrWhiteSpace(d.ServiceRequestId))
            dr["basedOn"] = new JsonArray(Fhir.Fhir.Reference("ServiceRequest", d.ServiceRequestId));
        if (d.Issued is { } issued) dr["issued"] = issued.ToString("o");
        if (!string.IsNullOrWhiteSpace(d.PresentedFormContentType))
        {
            var form = new JsonObject { ["contentType"] = d.PresentedFormContentType };
            if (!string.IsNullOrWhiteSpace(d.PresentedFormTitle)) form["title"] = d.PresentedFormTitle;
            dr["presentedForm"] = new JsonArray(form);
        }
        return dr;
    }

    public static JsonObject Encounter(EncounterSource e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var enc = Fhir.Fhir.Resource("Encounter", e.Id);
        enc["status"] = StatusMaps.Encounter(e.HbmpStatus);
        enc["class"] = Fhir.Fhir.Coding(Fhir.Fhir.Systems.EncounterClass, e.ClassCode ?? "AMB", "ambulatory");
        enc["subject"] = Fhir.Fhir.Reference("Patient", e.BeneficiaryId);
        if (e.Start is { } start)
        {
            var period = new JsonObject { ["start"] = start.ToString("o") };
            if (e.End is { } end) period["end"] = end.ToString("o");
            enc["period"] = period;
        }
        if (!string.IsNullOrWhiteSpace(e.PractitionerId))
            enc["participant"] = new JsonArray(new JsonObject
            {
                ["individual"] = Fhir.Fhir.Reference("Practitioner", e.PractitionerId),
            });
        return enc;
    }

    public static JsonObject Condition(ConditionSource c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var cond = Fhir.Fhir.Resource("Condition", c.Id);
        cond["clinicalStatus"] = Fhir.Fhir.CodeableConcept(
            Fhir.Fhir.Systems.ConditionClinical, StatusMaps.ConditionClinical(c.ClinicalStatus));
        cond["subject"] = Fhir.Fhir.Reference("Patient", c.BeneficiaryId);
        if (c.Code is { } code) cond["code"] = Fhir.Fhir.CodeableConcept(code.System, code.Code, code.Display);
        if (!string.IsNullOrWhiteSpace(c.EncounterId)) cond["encounter"] = Fhir.Fhir.Reference("Encounter", c.EncounterId);
        if (c.RecordedDate is { } rd) cond["recordedDate"] = rd.ToString("o");
        return cond;
    }

    public static JsonObject Observation(ObservationSource o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var obs = Fhir.Fhir.Resource("Observation", o.Id);
        obs["status"] = StatusMaps.Observation(o.HbmpStatus);
        obs["category"] = new JsonArray(Fhir.Fhir.CodeableConcept(
            Fhir.Fhir.Systems.ObservationCategory, o.Category ?? "vital-signs"));
        obs["subject"] = Fhir.Fhir.Reference("Patient", o.BeneficiaryId);
        if (o.Code is { } code) obs["code"] = Fhir.Fhir.CodeableConcept(code.System, code.Code, code.Display);
        if (o.Value is { } v)
            obs["valueQuantity"] = Fhir.Fhir.Quantity(v, o.Unit, string.IsNullOrWhiteSpace(o.UnitCode) ? null : Fhir.Fhir.Systems.Ucum, o.UnitCode);
        if (!string.IsNullOrWhiteSpace(o.EncounterId)) obs["encounter"] = Fhir.Fhir.Reference("Encounter", o.EncounterId);
        if (o.Effective is { } eff) obs["effectiveDateTime"] = eff.ToString("o");
        return obs;
    }

    public static JsonObject AllergyIntolerance(AllergyIntoleranceSource a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var ai = Fhir.Fhir.Resource("AllergyIntolerance", a.Id);
        ai["patient"] = Fhir.Fhir.Reference("Patient", a.BeneficiaryId);
        if (a.Code is { } code) ai["code"] = Fhir.Fhir.CodeableConcept(code.System, code.Code, code.Display);
        if (!string.IsNullOrWhiteSpace(a.Criticality)) ai["criticality"] = StatusMaps.AllergyCriticality(a.Criticality);
        if (!string.IsNullOrWhiteSpace(a.Reaction))
            ai["reaction"] = new JsonArray(new JsonObject
            {
                ["manifestation"] = new JsonArray(new JsonObject { ["text"] = a.Reaction }),
            });
        return ai;
    }

    private static string NormalizeGender(string? g) => (g ?? "").Trim().ToLowerInvariant() switch
    {
        "male" or "m" => "male",
        "female" or "f" => "female",
        "other" => "other",
        _ => "unknown",
    };
}
