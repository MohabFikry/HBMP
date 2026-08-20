import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import type { Encounter } from "@mersal/contracts";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DoctorEncounter } from "../src/screens/DoctorEncounter";
import type { ApiClient } from "../src/api/client";
import { seedSession } from "./helpers";

/**
 * 32.3 — correcting a SIGNED clinical note.
 *
 * ============================================================================================================
 * THE DEFECT THIS CLOSES
 * ============================================================================================================
 * emr has served `POST /encounters/{id}/notes/{noteId}/addendum` since phase 4.1, and its domain model calls
 * it "the ONLY way to correct after signing". This workspace tells the doctor so TWICE — once in the signed
 * banner ("Record a correction as an addendum") and again in the sign-off confirmation ("corrections can only
 * be added as an addendum").
 *
 * `HttpApiClient` had no method for it and no screen had a control. A doctor who signed a note with an error
 * in it had, in the only client this platform has, no way to correct the clinical record — and the 409 the
 * server returns on an edit attempt names a path the UI could not take.
 *
 * ============================================================================================================
 * WHAT AN ADDENDUM IS, AND WHAT THESE TESTS PROTECT
 * ============================================================================================================
 * It is an APPEND, not an edit. The original text stays exactly as signed and stays readable — that is the
 * whole value of the mechanism, and a UI that merged the correction into the original would have destroyed it
 * while appearing to implement it. So the first test asserts the correction appears AND the mistake is still
 * on screen.
 */

function encounter(over: Partial<Encounter> = {}): Encounter {
  return {
    id: "enc-77",
    patientId: "ben-9",
    patientName: { en: "Fatma Ibrahim", ar: "فاطمة إبراهيم" },
    openedAt: "2026-08-01T09:00:00Z",
    signed: true,
    noteId: "note-1",
    soap: {
      subjective: "Headache for two days.",
      objective: "BP 40/90.",
      assessment: "Tension headache.",
      plan: "Paracetamol PRN.",
    },
    vitals: {
      heightCm: null, weightKg: null, systolic: 140, diastolic: 90,
      heartRate: 72, tempC: 36.8, spo2: 98, measuredAt: "2026-08-01T09:15:00Z",
    },
    allergies: [],
    diagnoses: [],
    addenda: [],
    ...over,
  };
}

function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  return {
    getEncounter: vi.fn().mockResolvedValue(encounter()),
    patientProfile: vi.fn().mockResolvedValue({
      beneficiaryId: "ben-9", servedAt: "2026-08-01T09:00:00Z", sections: [],
    }),
    memberClinicalRecord: vi.fn().mockResolvedValue({
      beneficiaryId: "ben-9", bloodGroup: null, bloodGroupRecordedAt: null, allergies: [],
    }),
    medicationHistory: vi.fn().mockResolvedValue([]),
    listOrders: vi.fn().mockResolvedValue([]),
    listPrescriptions: vi.fn().mockResolvedValue([]),
    addNoteAddendum: vi.fn(),
    ...over,
  } as unknown as ApiClient;
}

function renderWorkspace(api: ApiClient) {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter
        initialEntries={["/clinician/encounter?encounter=enc-77"]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <DoctorEncounter />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("Addendum to a signed note (32.3)", () => {
  it("corrects a signed note, and leaves the mistake readable", async () => {
    const user = userEvent.setup();
    const addNoteAddendum = vi.fn().mockResolvedValue({
      id: "note-2",
      authoredAt: "2026-08-02T10:00:00Z",
      authoredByName: "Dr Karim Adel",
      soap: { subjective: "", objective: "Correction: BP was 140/90.", assessment: "", plan: "" },
    });
    renderWorkspace(fakeApi({ addNoteAddendum }));

    expect((await screen.findAllByText(/can no longer be edited/i)).length).toBeGreaterThan(0);

    await user.click(screen.getByRole("button", { name: /add addendum/i }));
    const form = await screen.findByRole("region", { name: /add addendum/i });
    await user.type(within(form).getByLabelText(/objective/i), "Correction: BP was 140/90.");
    await user.click(within(form).getByRole("button", { name: /save addendum/i }));

    await waitFor(() => expect(addNoteAddendum).toHaveBeenCalledWith("enc-77", "note-1",
      expect.objectContaining({ objective: "Correction: BP was 140/90." })));

    // THE POINT OF THE MECHANISM: the signed text is still there, wrong and legible. An addendum that
    // overwrote it would read as a tidier record and be a worse one.
    expect(screen.getByText(/BP 40\/90\./)).toBeInTheDocument();
  });

  it("shows an existing addendum beneath its original, with who wrote it and when", async () => {
    renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({
        addenda: [{
          id: "note-2",
          authoredAt: "2026-08-02T10:00:00Z",
          authoredByName: "Dr Karim Adel",
          soap: { subjective: "", objective: "Correction: BP was 140/90.", assessment: "", plan: "" },
        }],
      })),
    }));

    const addendum = await screen.findByRole("article", { name: /addendum/i });
    expect(within(addendum).getByText(/correction: bp was 140\/90/i)).toBeInTheDocument();
    // Attribution in words. A clinical correction signed by "22222222-2222-…" is unattributed in every
    // sense that matters to the next clinician reading it.
    expect(within(addendum).getByText(/dr karim adel/i)).toBeInTheDocument();
  });

  it("refuses an empty addendum, because the server does", async () => {
    const user = userEvent.setup();
    const addNoteAddendum = vi.fn();
    renderWorkspace(fakeApi({ addNoteAddendum }));

    await screen.findAllByText(/can no longer be edited/i);
    await user.click(screen.getByRole("button", { name: /add addendum/i }));
    const form = await screen.findByRole("region", { name: /add addendum/i });
    await user.click(within(form).getByRole("button", { name: /save addendum/i }));

    expect(await within(form).findByRole("alert")).toHaveTextContent(/at least one section/i);
    expect(addNoteAddendum).not.toHaveBeenCalled();
  });

  it("offers no addendum control while the note is still editable", async () => {
    // Before signing, the correction path is to type in the note. Offering both would present a doctor with
    // two ways to change the same unsigned text, one of which permanently splits it in two.
    renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({ signed: false })),
    }));

    await screen.findByRole("heading", { name: /subjective/i });
    expect(screen.queryByRole("button", { name: /add addendum/i })).not.toBeInTheDocument();
  });
});
