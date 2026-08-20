# Prescriber's Portal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the eight findings of the fifth client-vs-service pass, so the doctor's portal can do what the design set says it can — starting with an interaction check that stops reporting `Ok` about a comparison it never made.

**Architecture:** Six of the eight findings are wiring: the server serves the capability, the SPA has no method for it. Two are not. F1 is structural — the active-medication list is fetched data living in a request object, so it moves behind `Fetched<T>` on `ValidationSnapshot` where the ports gather it, following the precedent 28.2 set for diagnoses. F5b is a genuine server gap — `pharmacy` has no note endpoints, so prescription-line notes are ported from `orders/Api/Notes.cs` with the same entity shape and projection rules.

**Tech Stack:** .NET 8 (xunit, EF Core, minimal APIs), React 18 + TypeScript + zod (vitest, testing-library, axe), PostgreSQL 16 with RLS.

**Spec:** `docs/superpowers/specs/2026-08-20-prescriber-portal-design.md`

## Global Constraints

- **A clinical check may only ever warn, never block** — doc 43 invariant 1. Benefit rules block; clinical findings advise.
- **`Unavailable` is never `Ok`.** Every failure mode — transport error, timeout, non-2xx, unparseable body — maps to `Fetched<T>.Unavailable`. Phase 26 deleted three silent catches for this; do not add a fourth.
- **Coverage is stated, not implied** (doc 44 §8). A finding that says "none found" names what it searched.
- **Minimum-necessary at row and field level.** Project before serialization — "the screen does not show it" is not a control.
- **Every mutation writes an audit event** via the shared client. No hard deletes.
- **A note is not an amendment** (doc 46 §7b). Nothing in Gate 5 may supersede a line, bump `version_no`, or touch an authorisation.
- **Migrations are expand-only and idempotent under re-application.** Every migration in this repository re-runs on every pass.
- **Both locales, always.** Every user-visible string is `{ en, ar }`; a missing translation is a compile error.
- **`Idempotency-Key` is required** on `amend-schedule` and `cancel-lines`; the client generates one per user action, not per retry.
- **Test invocation on this machine:** from `apps/web`, `./node_modules/.bin/vitest run` and `./node_modules/.bin/tsc --noEmit` with `PATH="$HOME/.nvm/versions/node/v22.23.0/bin:$PATH"`. Never `pnpm`. Backend: `./dotnet.sh test --with-db <csproj>`.

---

## File Structure

**Gate 1 — the fetch seam (F1)**
- Modify `libs/clinical-validation/ValidationInputs.cs` — remove `ActiveMedicationDrugIds` from `ValidationRequest`; add `ActiveMedications` record and the `Fetched<ActiveMedications>` slot on `ValidationSnapshot`.
- Modify `libs/clinical-validation/PrescriptionValidator.cs` — read the new slot; three coverage sentences; `Unavailable` path.
- Modify `services/pharmacy/Infrastructure/ClinicalValidationPorts.cs` — the interface gains nothing; `FetchAsync` fills the new slot.
- Modify `services/pharmacy/Api/HttpClinicalValidationPorts.cs` — fetch the union.
- Modify `services/pharmacy/Api/PrescriptionValidationService.cs`, `services/pharmacy/Api/HttpClients.cs` — drop the `[]` argument.
- Test `libs/clinical-validation/Tests/` (existing suite), `services/pharmacy/Tests/`.

**Gate 2 — the writer (F7)**
- Modify `apps/web/src/screens/DoctorEncounter.tsx` — Current medications section.
- Modify `apps/web/src/api/{HttpApiClient,DevApiClient,client}.ts`, `libs/contracts/src/emr.ts`.
- Test `apps/web/test/encounter-medications.test.tsx`, `services/emr/Tests/`.

**Gate 3 — addendum (F2)** — `DoctorEncounter.tsx`, the three client files, `libs/contracts/src/emr.ts`; test `apps/web/test/encounter-addendum.test.tsx`.

**Gate 4 — result access (F3, F4)** — `apps/web/src/screens/ReportAccessInbox.tsx`, the three client files; test `apps/web/test/report-access-transitions.test.tsx`.

**Gate 5 — notes (F5)** — create `services/pharmacy/Api/Notes.cs`, `services/pharmacy/Infrastructure/Migrations/0021_prescription_note.sql`, `apps/web/src/screens/OrderNotes.tsx`; modify `PolicyPanels.tsx` (`NotesPanel` scope), the fulfiller queue screens; test `services/pharmacy/Tests/PrescriptionNoteTests.cs`, `apps/web/test/order-notes.test.tsx`.

**Gate 6 — chronic amendment (F6)** — `apps/web/src/screens/AmendLineDialog.tsx`, the three client files; test `apps/web/test/chronic-amend.test.tsx`.

**Gate 7 — close (F8)** — `HBMP-Design/50-the-prescribers-portal.md`, `docs/quality/invariant-registry.yaml`, `docs/BUILD-STATUS.md`.

---

### Task 1: Move the active-medication list behind the fetch seam

**Files:**
- Modify: `libs/clinical-validation/ValidationInputs.cs:40-43` (remove the field), `:407-420` (add the slot)
- Modify: `libs/clinical-validation/PrescriptionValidator.cs:234-261`
- Test: `libs/clinical-validation/Tests/InteractionTests.cs`

**Interfaces:**
- Consumes: `Fetched<T>`, `ProvenanceInfo`, `Finding.Clinical`, `ClinicalState` — all existing.
- Produces: `public sealed record ActiveMedications(IReadOnlyList<ActiveMedication> Items)` and `public sealed record ActiveMedication(Guid DrugId, string DrugName, string Source)` where `Source` is `"Prescribed" | "SelfReported" | "External"`. `ValidationSnapshot` gains a final parameter `Fetched<ActiveMedications> ActiveMedications`. `ValidationRequest` loses `ActiveMedicationDrugIds` and becomes `(Guid EncounterId, IReadOnlyList<PrescriptionLineInput> Lines)`.

- [ ] **Step 1: Write the failing test — an interaction with a drug the patient already takes is found**

```csharp
[Fact]
public void An_interaction_with_a_current_medication_is_reported()
{
    var warfarin = Guid.NewGuid();
    var aspirin = Guid.NewGuid();
    var request = new ValidationRequest(Guid.NewGuid(), [Line(aspirin, "Aspirin")]);
    var snapshot = Snapshot() with
    {
        Interactions = Fetched.From(TableWith(warfarin, aspirin), Prov()),
        ActiveMedications = Fetched.From(
            new ActiveMedications([new ActiveMedication(warfarin, "Warfarin", "Prescribed")]), Prov()),
    };

    var findings = PrescriptionValidator.Validate(request, snapshot, Now).Findings;

    var interaction = findings.Single(f => f.Kind == CheckKind.Interaction);
    Assert.Equal(ClinicalState.Warn, interaction.State);
    Assert.Contains("Warfarin", interaction.MessageEn);
}
```

- [ ] **Step 2: Write the second failing test — an unavailable source is never `Ok`**

```csharp
[Fact]
public void An_unavailable_active_medication_source_is_not_reported_as_clear()
{
    var request = new ValidationRequest(Guid.NewGuid(), [Line(Guid.NewGuid(), "Aspirin")]);
    var snapshot = Snapshot() with
    {
        ActiveMedications = new Fetched<ActiveMedications>.Unavailable("pharmacy unreachable"),
    };

    var findings = PrescriptionValidator.Validate(request, snapshot, Now).Findings;

    var interaction = findings.Single(f => f.Kind == CheckKind.Interaction);
    Assert.Equal(ClinicalState.Unavailable, interaction.State);
    Assert.DoesNotContain("No interaction found", interaction.MessageEn);
}
```

- [ ] **Step 3: Write the third failing test — "nothing recorded" is distinguishable from "nothing found"**

```csharp
[Fact]
public void No_recorded_medications_says_so_rather_than_claiming_a_comparison()
{
    var request = new ValidationRequest(Guid.NewGuid(), [Line(Guid.NewGuid(), "Aspirin")]);
    var snapshot = Snapshot() with
    {
        ActiveMedications = Fetched.From(new ActiveMedications([]), Prov()),
    };

    var interaction = PrescriptionValidator.Validate(request, snapshot, Now)
        .Findings.Single(f => f.Kind == CheckKind.Interaction);

    Assert.Equal(ClinicalState.Ok, interaction.State);
    Assert.Contains("no current medications recorded", interaction.MessageEn);
}
```

- [ ] **Step 4: Run the three tests and confirm they fail to COMPILE, not to assert**

Run: `./dotnet.sh test libs/clinical-validation/Tests/Mersal.ClinicalValidation.Tests.csproj`
Expected: build error — `ValidationSnapshot` has no `ActiveMedications`. A compile failure here is the correct first result: it is the type system refusing the old shape, which is the point of the move.

- [ ] **Step 5: Move the field**

In `ValidationInputs.cs`, delete `ActiveMedicationDrugIds` from `ValidationRequest` and add beside the other fetched facts:

```csharp
/// <summary>One medicine the beneficiary is already taking, and where that fact came from.</summary>
/// <remarks>
/// <paramref name="Source"/> is not decoration. A <c>Prescribed</c> row is Mersal's own record and is as
/// current as the dispensing log; a <c>SelfReported</c> one is what the patient said at a consultation and
/// may be months stale. The finding says which it compared against, because a prescriber weighing a warning
/// is entitled to know whether it rests on a dispensing record or on a recollection.
/// </remarks>
public sealed record ActiveMedication(Guid DrugId, string DrugName, string Source);

/// <summary>
/// What the beneficiary is already taking, as one fetched fact.
/// </summary>
/// <remarks>
/// This used to be <c>ValidationRequest.ActiveMedicationDrugIds</c> — a plain list on a request object, which
/// is the same shape the diagnoses had before 28.2 moved them here, and it failed the same way for the same
/// reason. Nothing fetched it, because it was not behind the fetch seam; no type complained, because an
/// empty list is a valid list; and every unflagged line was then reported <c>Ok</c> — "no interaction found"
/// — about a comparison that never happened. Behind <see cref="Fetched{T}"/> the empty case is
/// <c>Unavailable</c> with a reason, and there is no argument a call site can forget to pass.
/// </remarks>
public sealed record ActiveMedications(IReadOnlyList<ActiveMedication> Items);
```

Add `Fetched<ActiveMedications> ActiveMedications` as the last parameter of `ValidationSnapshot`.

- [ ] **Step 6: Rewrite the interaction block**

In `PrescriptionValidator.cs`, replace the `request.ActiveMedicationDrugIds` loop (`:234-245`) and the trailing `Ok` loop (`:248-261`):

```csharp
// Each line against what the beneficiary is already taking. Behind Fetched<T>, so "we could not ask"
// and "we asked and there is nothing" are different answers and neither is silence.
var actives = snapshot.ActiveMedications;
if (actives is Fetched<ActiveMedications>.Unavailable ua)
{
    foreach (var line in request.Lines)
    {
        findings.Add(Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Unavailable,
            $"Interaction check incomplete — the patient's current medications could not be read ({ua.Reason}). "
            + $"The lines being written now were checked against each other.",
            $"التحقق من التداخلات غير مكتمل — تعذّرت قراءة أدوية المريض الحالية ({ua.Reason}). "
            + $"تم التحقق من الأسطر المكتوبة الآن مقابل بعضها.",
            provenance));
    }
}
else
{
    var current = ((Fetched<ActiveMedications>.Available)actives).Value.Items;
    foreach (var line in request.Lines)
    {
        foreach (var active in current.DistinctBy(m => m.DrugId))
        {
            if (active.DrugId == line.DrugId) continue;
            if (!lookup.TryGetValue(Key(line.DrugId, active.DrugId), out var hit)) continue;

            findings.Add(Interaction(line, hit,
                $"with {active.DrugName}, which the patient is already taking ({active.Source})",
                $"مع {active.DrugName}، وهو دواء يتناوله المريض بالفعل ({active.Source})",
                provenance, relatedLineId: null));
            flagged.Add(line.LineId);
        }
    }

    // Coverage stated, not implied (doc 44 §8) — and the empty case says which emptiness it means.
    var coverageEn = current.Count == 0
        ? "no current medications recorded for this patient"
        : $"{current.Count} current medication(s)";
    var coverageAr = current.Count == 0
        ? "لا توجد أدوية حالية مسجّلة لهذا المريض"
        : $"{current.Count} من الأدوية الحالية";

    foreach (var line in request.Lines.Where(l => !flagged.Contains(l.LineId)))
    {
        findings.Add(Finding.Clinical(
            line.LineId, line.DrugId, CheckKind.Interaction, ClinicalState.Ok,
            $"No interaction found (checked against Mersal's interaction list: {table.KnownPairCount} "
            + $"ingredient pairs{Updated(table.RulesUpdatedAt)}, and {coverageEn}).",
            $"لم يتم العثور على تداخلات (تم التحقق مقابل قائمة التداخلات لدى مرسال: "
            + $"{table.KnownPairCount} زوجًا من المواد الفعالة{UpdatedAr(table.RulesUpdatedAt)}، و{coverageAr}).",
            provenance));
    }
}
```

- [ ] **Step 7: Run the library suite**

Run: `./dotnet.sh test libs/clinical-validation/Tests/Mersal.ClinicalValidation.Tests.csproj`
Expected: the three new tests PASS. Existing tests that construct `ValidationRequest` with three arguments fail to compile — fix each by dropping the third argument and adding `ActiveMedications = Fetched.From(new ActiveMedications([]), Prov())` to the snapshot builder. **Do not** add a default value to make them compile untouched: the point of the change is that every construction states its answer.

- [ ] **Step 8: Commit**

```bash
git add libs/clinical-validation
git commit -m "fix(clinical-validation): the interaction check said Ok about a comparison it never made"
```

---

### Task 2: Fetch the union — active prescriptions and recorded history

**Files:**
- Modify: `services/pharmacy/Api/HttpClinicalValidationPorts.cs:56` (`FetchAsync`)
- Modify: `services/pharmacy/Api/PrescriptionValidationService.cs:68`, `services/pharmacy/Api/HttpClients.cs:118`
- Test: `services/pharmacy/Tests/ActiveMedicationSourceTests.cs` (create)

**Interfaces:**
- Consumes: `ActiveMedications`, `ActiveMedication` from Task 1.
- Produces: nothing new; `FetchAsync` fills the slot it already returns.

- [ ] **Step 1: Write the failing test — the union is what reaches the engine**

```csharp
[Fact]
public async Task Active_prescriptions_and_recorded_history_both_reach_the_engine()
{
    await using var f = new PrescribingApiFactory();
    var beneficiary = await f.SeedBeneficiaryAsync();
    var fromRx = await f.SeedActivePrescriptionAsync(beneficiary, drugName: "Warfarin");
    var fromHistory = f.SeedMedicationHistory(beneficiary, drugName: "St John's Wort", source: "SelfReported");

    var snapshot = await f.Ports.FetchAsync(beneficiary, [Guid.NewGuid()], null, null, f.Bearer);

    var items = Assert.IsType<Fetched<ActiveMedications>.Available>(snapshot.ActiveMedications).Value.Items;
    Assert.Contains(items, m => m.DrugId == fromRx && m.Source == "Prescribed");
    Assert.Contains(items, m => m.DrugId == fromHistory && m.Source == "SelfReported");
}
```

- [ ] **Step 2: Write the failing test — an unreachable source is `Unavailable`, not empty**

```csharp
[Fact]
public async Task An_unreachable_history_source_is_Unavailable_rather_than_an_empty_list()
{
    await using var f = new PrescribingApiFactory(emrReachable: false);
    var beneficiary = await f.SeedBeneficiaryAsync();

    var snapshot = await f.Ports.FetchAsync(beneficiary, [Guid.NewGuid()], null, null, f.Bearer);

    var unavailable = Assert.IsType<Fetched<ActiveMedications>.Unavailable>(snapshot.ActiveMedications);
    Assert.Contains("emr", unavailable.Reason, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 3: Run and confirm both fail**

Run: `./dotnet.sh test --with-db services/pharmacy/Tests/Mersal.Pharmacy.Tests.csproj --filter ActiveMedicationSource`
Expected: FAIL — `FetchAsync` does not populate the slot.

- [ ] **Step 4: Implement the fetch**

In `HttpClinicalValidationPorts.FetchAsync`, add a gather beside the existing ones. The prescription half is a **local** query — pharmacy owns that table, so it is not an HTTP call:

```csharp
// Pharmacy's own record first: active lines on unexpired prescriptions for this beneficiary. A local
// read, because pharmacy owns the table — crossing a service boundary to ask itself a question would be
// slower and no more correct.
var prescribed = await db.PrescriptionLines.AsNoTracking()
    .Where(l => l.Prescription.BeneficiaryId == beneficiaryId
                && l.Status == PrescriptionLineStatus.Active
                && l.Prescription.ValidUntil >= clock.GetUtcNow())
    .Select(l => new ActiveMedication(l.DrugId, l.DrugName, "Prescribed"))
    .ToListAsync(ct);

// And what the patient reported taking that Mersal did not prescribe — the half no query over our own
// data can reconstruct. Unreachable emr makes the WHOLE fact Unavailable: a partial list presented as
// complete is the failure this type exists to prevent.
var history = await GetAsync<IReadOnlyList<MedicationHistoryDto>>(
    "emr", $"/api/v1/beneficiaries/{beneficiaryId}/medication-history?status=Active", bearerToken, ct);

var actives = history is null
    ? new Fetched<ActiveMedications>.Unavailable("emr medication history unreachable")
    : Fetched.From(new ActiveMedications([
        .. prescribed,
        .. history.Select(h => new ActiveMedication(h.DrugId, h.DrugName, h.Source)),
      ]), Provenance("mersal-prescriptions+emr-history"));
```

- [ ] **Step 5: Add the GET endpoint the fetch reads**

`services/emr/Api/ClinicalRecords.cs` serves `POST .../medication-history` but no GET. Add beside it, gated identically:

```csharp
ben.MapGet("/{beneficiaryId:guid}/medication-history", async (
    Guid beneficiaryId, string? status, EmrDbContext db, ClinicalGate gate, IAuditClient audit,
    IHbmpPrincipalAccessor me, CancellationToken ct) =>
{
    var denied = await gate.CheckAsync("emr:read", EmrPolicies.Resources.MedicationHistory,
        beneficiaryId.ToString(), beneficiaryId, ct);
    if (denied is not null) return denied;

    var q = db.MedicationHistories.AsNoTracking().Where(m => m.BeneficiaryId == beneficiaryId);
    if (Enum.TryParse<MedicationStatus>(status, out var s)) q = q.Where(m => m.Status == s);

    var rows = await q.OrderByDescending(m => m.StartDate).ToListAsync(ct);
    await EmitAsync(audit, "medication_history", beneficiaryId, AuditAction.Read, me, null, ct);
    return Results.Ok(rows.Select(MedicationHistoryResponse.From));
}).RequireAuthorization(HbmpPolicies.Scope("emr:read"))
  .Produces<IEnumerable<MedicationHistoryResponse>>();
```

- [ ] **Step 6: Delete the two `[]` arguments**

`PrescriptionValidationService.cs:68` and `HttpClients.cs:118` become `new ValidationRequest(encounterId, inputs)` / `new ValidationRequest(Guid.Empty, lines)`. **Delete `HttpClients.cs:117`'s comment** — it claims the empty list "produces NotChecked findings, which are reported rather than assumed away", which was never true and is now not even describable.

- [ ] **Step 7: Run pharmacy and emr suites**

Run: `./dotnet.sh test --with-db services/pharmacy/Tests/Mersal.Pharmacy.Tests.csproj` then the same for `services/emr/Tests/Mersal.Emr.Tests.csproj`
Expected: PASS, including the two new tests and the 182 pharmacy tests that already passed.

- [ ] **Step 8: Regenerate the specs and commit**

```bash
DOTNET=./dotnet.sh tools/ci/check-openapi-drift.sh --fix
git add services libs docs/api
git commit -m "feat(pharmacy,emr): the interaction check learns what the patient is already taking"
```

---

### Task 3: The current-medications section (F7's writer)

**Files:**
- Modify: `apps/web/src/screens/DoctorEncounter.tsx`, `apps/web/src/api/HttpApiClient.ts`, `DevApiClient.ts`, `client.ts`, `libs/contracts/src/emr.ts`
- Test: `apps/web/test/encounter-medications.test.tsx` (create), `apps/web/test/http-client-contract.test.ts` (extend)

**Interfaces:**
- Produces: `zMedicationHistoryRow = z.object({ medHistoryId, drugId, drugName, source: z.enum(["Prescribed","SelfReported","External"]), startDate: z.string().nullish(), endDate: z.string().nullish(), status: z.enum(["Active","Stopped"]) })`; client methods `medicationHistory(beneficiaryId): Promise<MedicationHistoryRow[]>`, `addMedicationHistory(beneficiaryId, req): Promise<MedicationHistoryRow>`, `stopMedication(beneficiaryId, medHistoryId): Promise<void>`.

- [ ] **Step 1: Write the failing screen test**

```tsx
it("records a medicine the patient is already taking, and shows its source", async () => {
  renderApp("/clinician/encounter?enc=ENC-1", { role: "doctor" });
  await screen.findByRole("heading", { name: /current medications/i });

  await userEvent.click(screen.getByRole("button", { name: /add medication/i }));
  await userEvent.type(screen.getByLabelText(/medicine/i), "Warfarin");
  await userEvent.click(await screen.findByRole("option", { name: /warfarin/i }));
  await userEvent.selectOptions(screen.getByLabelText(/source/i), "SelfReported");
  await userEvent.click(screen.getByRole("button", { name: /save/i }));

  const row = await screen.findByRole("row", { name: /warfarin/i });
  expect(within(row).getByText(/self-reported/i)).toBeInTheDocument();
});
```

- [ ] **Step 2: Write the contract test in `http-client-contract.test.ts`**

```ts
it("parses a medication-history list, and rejects a source the contract does not define", async () => {
  stubFetch([{ medHistoryId: "m1", drugId: "d1", drugName: "Warfarin", source: "SelfReported",
               startDate: null, endDate: null, status: "Active" }]);
  await expect(client.medicationHistory("b1")).resolves.toHaveLength(1);

  stubFetch([{ medHistoryId: "m1", drugId: "d1", drugName: "W", source: "ok", status: "Active" }]);
  await expect(client.medicationHistory("b1")).rejects.toThrow(/contract validation/i);
});
```

- [ ] **Step 3: Run both, confirm failure**

Run: `PATH="$HOME/.nvm/versions/node/v22.23.0/bin:$PATH" ./node_modules/.bin/vitest run test/encounter-medications.test.tsx test/http-client-contract.test.ts`
Expected: FAIL — no such heading, no such client method.

- [ ] **Step 4: Add the contract, the three client methods and the DevApiClient fixtures**

The fixture rows use the real vocabulary — a `Prescribed` row and a `SelfReported` one, so the screen's two cases both render in dev.

- [ ] **Step 5: Add the section to `DoctorEncounter.tsx`**

A table of drug name, source, started, status; an **Add medication** dialog with the drug combobox the prescribing workspace already uses (`DrugCombobox`), a source select, optional start date; and a **Mark stopped** action per active row. Helper text states why it is being asked: *"What the patient is already taking, including medicines Mersal did not prescribe. The interaction check reads this."*

- [ ] **Step 6: Run the two suites, then the whole web suite**

Run: `./node_modules/.bin/vitest run` and `./node_modules/.bin/tsc --noEmit`
Expected: PASS, 1487 + the new tests, axe clean on the new controls.

- [ ] **Step 7: Commit**

```bash
git add apps/web libs/contracts
git commit -m "feat(web): record what the patient is already taking, where the check can read it"
```

---

### Task 4: The addendum control (F2)

**Files:**
- Modify: `apps/web/src/screens/DoctorEncounter.tsx:198,211` (the strings that promise it), `apps/web/src/api/HttpApiClient.ts:1184` (the filter that becomes a grouping)
- Test: `apps/web/test/encounter-addendum.test.tsx` (create)

**Interfaces:**
- Produces: `addNoteAddendum(encounterId, noteId, req: { subjective?, objective?, assessment?, plan? }): Promise<NoteView>`; `notes()` returns primaries with `addenda: NoteView[]` attached rather than filtering addenda away.

- [ ] **Step 1: Write the failing test**

```tsx
it("corrects a signed note with an addendum, and leaves the original readable", async () => {
  renderApp("/clinician/encounter?enc=ENC-SIGNED", { role: "doctor" });
  expect(await screen.findByText(/signed and can no longer be edited/i)).toBeInTheDocument();

  await userEvent.click(screen.getByRole("button", { name: /add addendum/i }));
  await userEvent.type(screen.getByLabelText(/assessment/i), "Correction: BP was 140/90, not 40/90.");
  await userEvent.click(screen.getByRole("button", { name: /save addendum/i }));

  expect(await screen.findByText(/correction: bp was 140\/90/i)).toBeInTheDocument();
  expect(screen.getByText(/40\/90/)).toBeInTheDocument();   // the original is still there
});
```

- [ ] **Step 2: Write the empty-addendum test**

```tsx
it("refuses an empty addendum, because the server does", async () => {
  renderApp("/clinician/encounter?enc=ENC-SIGNED", { role: "doctor" });
  await userEvent.click(screen.getByRole("button", { name: /add addendum/i }));
  await userEvent.click(screen.getByRole("button", { name: /save addendum/i }));
  expect(await screen.findByRole("alert")).toHaveTextContent(/at least one section/i);
});
```

- [ ] **Step 3: Run, confirm failure**

Run: `./node_modules/.bin/vitest run test/encounter-addendum.test.tsx`
Expected: FAIL — no "Add addendum" button exists.

- [ ] **Step 4: Implement**

Client method posts to `/encounters/${enc}/notes/${noteId}/addendum`. `notes()` groups by `addendumOfNoteId` instead of discarding. The signed-note block gains the action; the composer is the existing S/O/A/P form with its own submit label. An addendum renders indented beneath its original with author and timestamp, never merged into it.

- [ ] **Step 5: Run the web suite and commit**

```bash
git add apps/web libs/contracts
git commit -m "feat(web): a signed note can be corrected the only way the record allows"
```

---

### Task 5: Both result-access transitions (F3, F4)

**Files:**
- Modify: `apps/web/src/screens/ReportAccessInbox.tsx:118-120`, the three client files
- Test: `apps/web/test/report-access-transitions.test.tsx` (create)

**Interfaces:**
- Produces: `takeReportAccessUnderReview(requestId): Promise<{ status: string }>`, `supplyReportAccessInfo(requestId, supplement: string): Promise<{ status: string }>`.

- [ ] **Step 1: Write the failing test — the door opens both ways**

```tsx
it("lets a requester answer a reviewer's question, which is the only way out of InfoRequested", async () => {
  renderApp("/clinician/result-access", { role: "doctor", as: "requester" });
  const row = await screen.findByRole("row", { name: /info requested/i });

  await userEvent.click(within(row).getByRole("button", { name: /respond/i }));
  await userEvent.type(screen.getByLabelText(/supplement/i), "Treating clinician since 2026-06; needed for the follow-up.");
  await userEvent.click(screen.getByRole("button", { name: /send/i }));

  expect(await screen.findByRole("row", { name: /under review/i })).toBeInTheDocument();
  expect(screen.getByText(/treating clinician since 2026-06/i)).toBeInTheDocument();
});
```

- [ ] **Step 2: Write the pick-up test — explicit, never on render**

```tsx
it("takes a request under review only when asked, never by being looked at", async () => {
  const posts: string[] = [];
  renderApp("/clinician/result-access", { role: "doctor", onPost: (u) => posts.push(u) });
  await screen.findByRole("row", { name: /awaiting decision/i });
  expect(posts.filter((u) => u.includes("/review"))).toHaveLength(0);

  await userEvent.click(screen.getByRole("button", { name: /take under review/i }));
  expect(posts.filter((u) => u.includes("/review"))).toHaveLength(1);
});
```

- [ ] **Step 3: Run, confirm failure**

Run: `./node_modules/.bin/vitest run test/report-access-transitions.test.tsx`
Expected: FAIL — neither control exists.

- [ ] **Step 4: Implement**

Two client methods; a **Take under review** button on `Requested` rows; a **Respond** control on the requester's own `InfoRequested` rows showing the original justification above the supplement field. Decision buttons stay available without pick-up — the server permits it and the screen must not invent a stricter rule.

- [ ] **Step 5: Run the web suite and commit**

```bash
git add apps/web libs/contracts
git commit -m "feat(web): the request that could be asked a question can now answer it"
```

---

### Task 6: Prescription-line notes, server side (F5b)

**Files:**
- Create: `services/pharmacy/Infrastructure/Migrations/0021_prescription_note.sql`, `services/pharmacy/Api/Notes.cs`, `services/pharmacy/Tests/PrescriptionNoteTests.cs`
- Modify: `services/pharmacy/Domain/Entities.cs`, `services/pharmacy/Infrastructure/PharmacyDbContext.cs`, `services/pharmacy/Api/Program.cs`

**Interfaces:**
- Produces: `GET|POST /api/v1/prescriptions/{rxId}/lines/{lineId}/notes`, `POST /api/v1/prescriptions/notes/{noteId}/cancel`; `PrescriptionNote` entity mirroring `OrderNote`; `NoteResponse` shape identical to orders'.

- [ ] **Step 1: Write the migration**

```sql
-- 32.5b (design 46 §7b) — notes on a prescription line. The orders twin is orders.order_note; this is the
-- same model on a different subject, per the doc's own instruction not to build a second mechanism.
CREATE TABLE IF NOT EXISTS pharmacy.prescription_note (
    note_id             uuid PRIMARY KEY,
    tenant_id           text NOT NULL,
    subject_type        text NOT NULL DEFAULT 'PrescriptionLine',
    subject_id          uuid NOT NULL,
    root_line_id        uuid NOT NULL,
    visibility          text NOT NULL,
    body                text NOT NULL,
    author_user_id      uuid NOT NULL,
    author_display_name text NOT NULL,
    authored_at         timestamptz NOT NULL,
    status              text NOT NULL DEFAULT 'Active',
    cancelled_at        timestamptz,
    cancel_reason       text,
    CONSTRAINT ck_rx_note_visibility CHECK (visibility IN ('ToFulfiller','Internal','FromFulfiller')),
    CONSTRAINT ck_rx_note_cancel_pair CHECK ((cancelled_at IS NULL) = (cancel_reason IS NULL)),
    CONSTRAINT ck_rx_note_len CHECK (char_length(body) <= 500)
);
CREATE INDEX IF NOT EXISTS ix_rx_note_root ON pharmacy.prescription_note (root_line_id, authored_at DESC);
ALTER TABLE pharmacy.prescription_note ENABLE ROW LEVEL SECURITY;
```

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public async Task A_note_is_not_an_amendment() { /* write a note; assert version_no and
    authorization_id on the line are byte-identical before and after, and no outbox event of type
    RxAmended was enqueued */ }

[Fact] public async Task A_fulfiller_may_only_write_FromFulfiller() { /* 403 with provider-note-class */ }

[Fact] public async Task A_note_on_a_restricted_line_inherits_that_restriction() { /* 403 note-restricted
    for a non-author reader */ }

[Fact] public async Task A_cancelled_note_stays_visible_struck_through() { /* status Cancelled, body still
    returned, never deleted */ }
```

- [ ] **Step 3: Run and confirm failure**

Run: `./dotnet.sh test --with-db services/pharmacy/Tests/Mersal.Pharmacy.Tests.csproj --filter PrescriptionNote`
Expected: FAIL — 404, no route.

- [ ] **Step 4: Port `orders/Api/Notes.cs`**

Same structure, `PharmacyGate` in place of `OrdersGate`, `SensitiveDisclosure` on the line's sensitivity, `NoteAudience.Readable` before serialization, the identical 500-char message about clinical findings belonging in the encounter note. Register in `Program.cs`.

- [ ] **Step 5: Run, regenerate specs, replay the migration twice, commit**

```bash
./dotnet.sh test --with-db services/pharmacy/Tests/Mersal.Pharmacy.Tests.csproj
DOTNET=./dotnet.sh tools/ci/check-openapi-drift.sh --fix
git add services docs/api && git commit -m "feat(pharmacy): prescriptions get the notes the design gave every other order kind"
```

---

### Task 7: The notes panel, doctor and fulfiller (F5a)

**Files:**
- Modify: `apps/web/src/screens/PolicyPanels.tsx:213` (`NotesPanel` gains the order-line scope), `LabQueue.tsx`, `ProcedureCentre.tsx`, `PharmacyDispense.tsx`, `DoctorEncounter.tsx`, the three client files
- Test: `apps/web/test/order-notes.test.tsx` (create)

**Interfaces:**
- Produces: `lineNotes(kind: "investigation"|"procedure"|"prescription", orderId, lineId)`, `writeLineNote(kind, orderId, lineId, body, visibility)`, `cancelLineNote(kind, noteId, reason)`.

- [ ] **Step 1: Write the failing tests**

```tsx
it("sends an instruction with an order, and the fulfiller reads it on the queue detail", async () => {
  renderApp("/clinician/orders", { role: "doctor" });
  await userEvent.click(await screen.findByRole("button", { name: /add note/i }));
  await userEvent.type(screen.getByLabelText(/note/i), "Fasting sample please.");
  await userEvent.click(screen.getByRole("button", { name: /save note/i }));
  expect(await screen.findByText(/fasting sample please/i)).toBeInTheDocument();

  renderApp("/lab/queue", { role: "lab" });
  await userEvent.click(await screen.findByRole("button", { name: /ORD-1/i }));
  expect(await screen.findByText(/fasting sample please/i)).toBeInTheDocument();
});

it("never offers a fulfiller the two classes they may not write", async () => {
  renderApp("/lab/queue", { role: "lab" });
  await userEvent.click(await screen.findByRole("button", { name: /ORD-1/i }));
  await userEvent.click(screen.getByRole("button", { name: /add note/i }));
  const options = within(screen.getByLabelText(/visibility/i)).getAllByRole("option");
  expect(options.map((o) => o.textContent)).toEqual(["Reply to the ordering clinician"]);
});
```

- [ ] **Step 2: Run, confirm failure. Step 3: implement. Step 4: run the whole web suite.**

Run: `./node_modules/.bin/vitest run && ./node_modules/.bin/tsc --noEmit`

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(web): the instruction that nobody could read"
```

---

### Task 8: The chronic amendment dialog (F6)

**Files:**
- Modify: `apps/web/src/screens/AmendLineDialog.tsx`, the three client files
- Test: `apps/web/test/chronic-amend.test.tsx` (create)

**Interfaces:**
- Produces: `amendChronicSchedule(rxId, lineId, req: { durationDays, frequencyMonths, reasonCode, reasonText?, convertToAcute? })`, `cancelPrescriptionLines(rxId, reasonCode, reasonText?)`.

- [ ] **Step 1: Write the three refusal tests**

```tsx
it("refuses a total below what has already been dispensed, in words", async () => {
  await openChronicAmend({ dispensedPacks: 3 });
  await userEvent.clear(screen.getByLabelText(/duration/i));
  await userEvent.type(screen.getByLabelText(/duration/i), "10");
  expect(await screen.findByRole("alert")).toHaveTextContent(/already collected/i);
  expect(screen.getByRole("button", { name: /save/i })).toBeDisabled();
});

it("asks for explicit confirmation before turning a chronic script acute", async () => {
  await openChronicAmend({});
  await userEvent.clear(screen.getByLabelText(/duration/i));
  await userEvent.type(screen.getByLabelText(/duration/i), "20");
  expect(await screen.findByText(/no longer a chronic prescription/i)).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /save/i })).toBeDisabled();
  await userEvent.click(screen.getByRole("checkbox", { name: /convert it to acute/i }));
  expect(screen.getByRole("button", { name: /save/i })).toBeEnabled();
});

it("says the script returns for authorisation when the amendment leaves the approved scope", async () => {
  await openChronicAmend({ authorised: true });
  await userEvent.clear(screen.getByLabelText(/duration/i));
  await userEvent.type(screen.getByLabelText(/duration/i), "180");
  expect(await screen.findByText(/returns for authorisation/i)).toBeInTheDocument();
});
```

- [ ] **Step 2: Run, confirm failure. Step 3: implement with the allocation preview. Step 4: run the suite. Step 5: commit.**

```bash
git commit -am "feat(web): the chronic amendment that was built, debugged and unreachable"
```

---

### Task 9: Close the pass

**Files:**
- Create: `HBMP-Design/50-the-prescribers-portal.md`
- Modify: `HBMP-Design/00-README-INDEX.md`, `docs/quality/invariant-registry.yaml`, `docs/BUILD-STATUS.md`

- [ ] **Step 1: Decide F8 by reading it**

Compare `GET /prescriptions/{id}/dispensing`'s projection against `queue`/`search`. Superset → wire it and drop the duplication. Subset → delete the endpoint. Record which and why in the design doc; do not leave it served, unreached and unexplained.

- [ ] **Step 2: Register the invariants**

```yaml
- id: INV-A-CHECK-NEVER-REPORTS-OK-ABOUT-A-COMPARISON-IT-DID-NOT-MAKE
  proven_by: [libs/clinical-validation/Tests/InteractionTests.cs, services/pharmacy/Tests/ActiveMedicationSourceTests.cs]
- id: INV-A-SIGNED-CLINICAL-NOTE-HAS-A-CORRECTION-PATH
  proven_by: [apps/web/test/encounter-addendum.test.tsx]
- id: INV-NO-STATE-THE-PRODUCT-CAN-ENTER-AND-NOT-LEAVE
  proven_by: [apps/web/test/report-access-transitions.test.tsx]
- id: INV-A-NOTE-IS-NEVER-AN-AMENDMENT
  proven_by: [services/pharmacy/Tests/PrescriptionNoteTests.cs]
```

- [ ] **Step 3: Run every gate**

```bash
./dotnet.sh test HbmpPlatform.sln -c Release --with-db
cd apps/web && ./node_modules/.bin/vitest run && ./node_modules/.bin/tsc --noEmit
DOTNET=./dotnet.sh tools/ci/check-openapi-drift.sh
tools/ci/apply-migrations.sh && tools/ci/apply-migrations.sh
for g in tools/ci/check-*.py; do python3 "$g" 2>&1 | tail -1; done
```

- [ ] **Step 4: Commit and open the PR**
