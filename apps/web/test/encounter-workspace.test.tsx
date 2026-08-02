import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { Encounter } from "@mersal/contracts";
import { DoctorEncounter } from "../src/screens/DoctorEncounter";

function encounter(over: Partial<Encounter> = {}): Encounter {
  return {
    id: "enc-77",
    patientId: "ben-9",
    patientName: { en: "Fatma Ibrahim", ar: "فاطمة إبراهيم" },
    openedAt: "2026-08-01T09:00:00Z",
    signed: false,
    noteId: null,
    soap: { subjective: "", objective: "", assessment: "", plan: "" },
    vitals: {
      heightCm: null, weightKg: null, systolic: 118, diastolic: 76,
      heartRate: 72, tempC: 38.2, spo2: 98, measuredAt: "2026-08-01T09:15:00Z",
    },
    allergies: [],
    diagnoses: [],
    ...over,
  };
}

/**
 * A complete client. `getEncounter` and `patientProfile` are called on open — the context strip reads the
 * profile header — so a fixture missing either fails on an unhandled rejection, which is a fault in the test
 * rather than in the screen and surfaces as noise instead of as a failure. The rest are here for the tabs
 * that fetch only once opened.
 */
function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  return {
    getEncounter: vi.fn().mockResolvedValue(encounter()),
    saveEncounterNote: vi.fn().mockResolvedValue({ noteId: "note-1" }),
    signEncounterNote: vi.fn().mockResolvedValue(undefined),
    addEncounterDiagnosis: vi.fn(),
    removeEncounterDiagnosis: vi.fn().mockResolvedValue(undefined),
    searchIcd: vi.fn().mockResolvedValue([]),
    listPatients: vi.fn().mockResolvedValue([]),
    ordersMine: vi.fn().mockResolvedValue([]),
    prescriptionsMine: vi.fn().mockResolvedValue([]),
    recordVitals: vi.fn().mockResolvedValue({ encounterId: "enc-77", recorded: 1 }),
    // The history's encounters table resolves branch labels and practitioner names in the browser (emr owns
    // neither). Omitting these is an unhandled rejection inside the table, not a failure.
    branchLabels: vi.fn().mockResolvedValue(new Map()),
    practitioners: vi.fn().mockResolvedValue([]),
    patientProfile: vi.fn().mockResolvedValue({
      beneficiaryId: "ben-9", servedAt: "2026-08-01T09:00:00Z",
      sections: [{
        key: "header", state: "Visible", variant: "full", data: {
          beneficiaryId: "ben-9", memberNo: "MRS-M-884291", displayName: "Fatma Ibrahim",
          status: "Active", statusCue: { label: "Active", tone: "ok", shape: "circle" },
          sex: "Female", birthDate: "1993-04-11", relationship: "Principal",
        },
      }],
    }),
    ...over,
  } as unknown as ApiClient;
}

function renderWorkspace(api: ApiClient, entry = "/clinician/encounter?encounter=enc-77") {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter initialEntries={[entry]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <DoctorEncounter />
      </MemoryRouter>
    </AppProviders>,
  );
}

/**
 * The encounter workspace (US-030 / US-031) — the screen "Start visit" opens.
 *
 * What is being proven here is that the consultation can be WRITTEN. The screen this replaced rendered the
 * same four SOAP sections as read-only text, so "Start visit" opened a page on which the note could not be
 * taken; every assertion below is about the difference.
 */
describe("Encounter workspace (US-031)", () => {
  it("opens the encounter named in the query string, with the four SOAP sections editable", async () => {
    const getEncounter = vi.fn().mockResolvedValue(encounter());
    renderWorkspace(fakeApi({ getEncounter }));

    await waitFor(() => expect(getEncounter).toHaveBeenCalledWith("enc-77"));
    for (const heading of ["Subjective", "Objective", "Assessment", "Plan"]) {
      // Named by its heading, and writable — the whole point of the screen.
      const box = await screen.findByRole("textbox", { name: heading });
      expect(box).not.toHaveAttribute("readonly");
    }
  });

  it("saves a draft against the note the encounter already has", async () => {
    const user = userEvent.setup();
    const saveEncounterNote = vi.fn().mockResolvedValue({ noteId: "note-1" });
    renderWorkspace(fakeApi({
      saveEncounterNote,
      getEncounter: vi.fn().mockResolvedValue(encounter({ noteId: "note-1" })),
    }));

    await user.type(await screen.findByRole("textbox", { name: "Subjective" }), "Cough for 5 days");
    await user.click(screen.getByRole("button", { name: /save draft/i }));

    // The EXISTING note id, not null: a second POST would leave a second partial note on the encounter.
    await waitFor(() => expect(saveEncounterNote).toHaveBeenCalledWith(
      "enc-77", "note-1", expect.objectContaining({ subjective: "Cough for 5 days" }),
    ));
  });

  it("opens the first note with a create, not an update", async () => {
    const user = userEvent.setup();
    const saveEncounterNote = vi.fn().mockResolvedValue({ noteId: "note-new" });
    renderWorkspace(fakeApi({ saveEncounterNote }));

    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Supportive care");
    await user.click(screen.getByRole("button", { name: /save draft/i }));

    await waitFor(() => expect(saveEncounterNote).toHaveBeenCalledWith("enc-77", null, expect.anything()));
  });

  it("will not save an empty note", async () => {
    renderWorkspace(fakeApi());
    // Nothing written in any section — emr answers 422 for this, and offering the button anyway would send
    // the doctor to a failure the screen could see coming.
    expect(await screen.findByRole("button", { name: /save draft/i })).toBeDisabled();
    expect(screen.getByRole("button", { name: /save & finalize/i })).toBeDisabled();
  });

  it("asks before signing, then saves and signs in that order", async () => {
    const user = userEvent.setup();
    const saveEncounterNote = vi.fn().mockResolvedValue({ noteId: "note-1" });
    const signEncounterNote = vi.fn().mockResolvedValue(undefined);
    renderWorkspace(fakeApi({
      saveEncounterNote, signEncounterNote,
      // A primary diagnosis is required to finalize — see the rule's own test below.
      getEncounter: vi.fn().mockResolvedValue(encounter({
        diagnoses: [{ id: "dx-1", system: "ICD-10", code: "J01.90", rank: "Primary",
                      label: { en: "Acute sinusitis", ar: "التهاب جيوب" } }],
      })),
    }));

    await user.type(await screen.findByRole("textbox", { name: "Assessment" }), "Acute sinusitis");
    await user.click(screen.getByRole("button", { name: /save & finalize/i }));

    // Signing is irreversible, so it is confirmed — and nothing has been signed yet at this point.
    expect(await screen.findByText(/signing locks the note/i)).toBeInTheDocument();
    expect(signEncounterNote).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: /sign & lock/i }));
    await waitFor(() => expect(signEncounterNote).toHaveBeenCalledWith("enc-77", "note-1"));
    // Saved BEFORE signing: signing a note that was never written would lock an empty record.
    expect(saveEncounterNote).toHaveBeenCalled();
    expect(saveEncounterNote.mock.invocationCallOrder[0])
      .toBeLessThan(signEncounterNote.mock.invocationCallOrder[0]);
  });

  it("a signed note is read-only and says why", async () => {
    renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({
        signed: true, noteId: "note-1",
        soap: { subjective: "Cough", objective: "", assessment: "", plan: "" },
      })),
    }));

    expect(await screen.findByRole("textbox", { name: "Subjective" })).toHaveAttribute("readonly");
    // "Read-only" alone teaches nothing; the addendum is the way forward and is named.
    expect(screen.getAllByText(/addendum/i).length).toBeGreaterThan(0);
    expect(screen.queryByRole("button", { name: /save & finalize/i })).not.toBeInTheDocument();
  });

  it("a 403 on save names the author rule instead of blaming the service", async () => {
    const user = userEvent.setup();
    renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({ noteId: "note-1" })),
      saveEncounterNote: vi.fn().mockRejectedValue(new ApiError("http", "not-author", 403)),
    }));

    await user.type(await screen.findByRole("textbox", { name: "Objective" }), "Chest clear");
    await user.click(screen.getByRole("button", { name: /save draft/i }));

    // A covering doctor may read a colleague's encounter and may not overwrite it. "Save failed" would send
    // them retrying something that can never succeed.
    expect(await screen.findByText(/only the note's author/i)).toBeInTheDocument();
  });

  it("records a diagnosis at the rank the doctor chose, defaulting the first one to primary", async () => {
    const user = userEvent.setup();
    const addEncounterDiagnosis = vi.fn().mockResolvedValue({
      id: "dx-1", system: "ICD-10", code: "J01.90", rank: "Primary",
      label: { en: "Acute sinusitis, unspecified", ar: "التهاب جيوب حاد" },
    });
    renderWorkspace(fakeApi({
      addEncounterDiagnosis,
      searchIcd: vi.fn().mockResolvedValue([{ code: "J01.90", title: "Acute sinusitis, unspecified" }]),
    }));

    await user.click(await screen.findByRole("button", { name: /add diagnosis/i }));
    await user.type(screen.getByRole("textbox", { name: /search icd-10/i }), "sinus");
    await user.click(await screen.findByRole("button", { name: /J01\.90/ }));

    // STAGED, not recorded. Nothing reaches emr until the doctor commits the set.
    expect(addEncounterDiagnosis).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: /^add$/i }));

    // Primary by default while the encounter has none — the commonest first pick, pre-selected.
    await waitFor(() => expect(addEncounterDiagnosis).toHaveBeenCalledWith("enc-77", "J01.90", "Primary"));
    expect(await screen.findByText("Acute sinusitis, unspecified")).toBeInTheDocument();
  });

  it("stages several codes and records them in one pass, the first as primary", async () => {
    const user = userEvent.setup();
    const addEncounterDiagnosis = vi.fn(async (_e: string, code: string, rank: string) => ({
      id: `dx-${code}`, system: "ICD-10", code, rank,
      label: { en: code, ar: code },
    }));
    renderWorkspace(fakeApi({
      addEncounterDiagnosis: addEncounterDiagnosis as never,
      searchIcd: vi.fn().mockResolvedValue([
        { code: "J01.90", title: "Acute sinusitis, unspecified" },
        { code: "I10", title: "Essential hypertension" },
      ]),
    }));

    await user.click(await screen.findByRole("button", { name: /add diagnosis/i }));
    await user.type(screen.getByRole("textbox", { name: /search icd-10/i }), "ac");
    await user.click(await screen.findByRole("button", { name: /J01\.90/ }));
    await user.click(screen.getByRole("button", { name: /I10/ }));

    // A consultation that ends in a primary plus a comorbidity is the ordinary case; it used to mean
    // opening this dialog twice and retyping the search.
    await user.click(screen.getByRole("button", { name: /add 2 diagnoses/i }));

    await waitFor(() => expect(addEncounterDiagnosis).toHaveBeenCalledTimes(2));
    expect(addEncounterDiagnosis).toHaveBeenNthCalledWith(1, "enc-77", "J01.90", "Primary");
    // Only ONE primary comes out of a batch — the second pick defaults to secondary.
    expect(addEncounterDiagnosis).toHaveBeenNthCalledWith(2, "enc-77", "I10", "Secondary");
  });

  it("groups primary apart from secondary", async () => {
    renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({
        diagnoses: [
          { id: "dx-1", system: "ICD-10", code: "J01.90", rank: "Primary",
            label: { en: "Acute sinusitis", ar: "التهاب جيوب" } },
          { id: "dx-2", system: "ICD-10", code: "I10", rank: "Secondary",
            label: { en: "Essential hypertension", ar: "ارتفاع ضغط الدم" } },
        ],
      })),
    }));

    // The rank is a HEADING, not just a chip tint: which code the claim and the authorization key on must
    // not come down to a border colour.
    expect(await screen.findByRole("heading", { name: "Primary" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Secondary" })).toBeInTheDocument();
    expect(screen.getByText("Acute sinusitis").closest(".dx-chip")).toHaveClass("dx-chip--primary");
    expect(screen.getByText("Essential hypertension").closest(".dx-chip")).not.toHaveClass("dx-chip--primary");
  });

  it("will not finalize an encounter with no primary diagnosis, and says why", async () => {
    const user = userEvent.setup();
    const signEncounterNote = vi.fn();
    renderWorkspace(fakeApi({
      signEncounterNote,
      getEncounter: vi.fn().mockResolvedValue(encounter({
        diagnoses: [{ id: "dx-2", system: "ICD-10", code: "I10", rank: "Secondary",
                      label: { en: "Essential hypertension", ar: "ارتفاع ضغط الدم" } }],
      })),
    }));

    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Supportive care");
    expect(screen.getByRole("button", { name: /save & finalize/i })).toBeDisabled();
    // The reason is on screen, not discovered by pressing a dead button.
    expect(screen.getAllByText(/primary diagnosis/i).length).toBeGreaterThan(0);
    expect(signEncounterNote).not.toHaveBeenCalled();
  });

  it("retracts a coded diagnosis, and offers no retract once the note is signed", async () => {
    const user = userEvent.setup();
    const dx = {
      id: "dx-1", system: "ICD-10" as const, code: "J01.90", rank: "Primary" as const,
      label: { en: "Acute sinusitis, unspecified", ar: "التهاب جيوب حاد" },
    };
    const removeEncounterDiagnosis = vi.fn().mockResolvedValue(undefined);
    const view = renderWorkspace(fakeApi({
      removeEncounterDiagnosis,
      getEncounter: vi.fn().mockResolvedValue(encounter({ diagnoses: [dx] })),
    }));

    await user.click(await screen.findByRole("button", { name: /retract J01\.90/i }));
    await waitFor(() => expect(removeEncounterDiagnosis).toHaveBeenCalledWith("enc-77", "dx-1"));
    view.unmount();

    renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({
        diagnoses: [dx], signed: true, noteId: "note-1",
        soap: { subjective: "Cough", objective: "", assessment: "", plan: "" },
      })),
    }));
    // The sign-lock reaches the assessment too: after signing, a coded diagnosis is a signed clinical
    // statement and the correction path is an addendum, not a cross on a chip.
    expect(await screen.findByText("J01.90")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /retract J01\.90/i })).not.toBeInTheDocument();
  });

  it("shows the blood pressure as a pair and flags a reading outside its reference band", async () => {
    const { container } = renderWorkspace(fakeApi());

    // Both halves. A lone systolic is not a blood pressure — 118 is unremarkable over 76 and an emergency
    // over 118, and the panel must not be able to render those identically.
    expect(await screen.findByText("118 / 76")).toBeInTheDocument();

    // Scoped to the rail: Radix keeps every tab panel mounted, so the vitals-capture form's own
    // "Temperature" field label is in the document too.
    const panel = within(container.querySelector(".vitals-panel") as HTMLElement);
    // 38.2°C against a 36.3–37.2 band. Flagged in WORDS, not by hue alone.
    const temp = panel.getByText("Temperature").closest(".vital-row")!;
    expect(within(temp as HTMLElement).getByText("High")).toBeInTheDocument();
    const hr = panel.getByText("Heart rate").closest(".vital-row")!;
    expect(within(hr as HTMLElement).getByText("In range")).toBeInTheDocument();
  });

  it("records a blood pressure as two readings", async () => {
    const user = userEvent.setup();
    const recordVitals = vi.fn().mockResolvedValue({ encounterId: "enc-77", recorded: 2 });
    renderWorkspace(fakeApi({ recordVitals }));

    await user.click(await screen.findByRole("button", { name: /record vitals/i }));
    await user.type(await screen.findByRole("spinbutton", { name: /systolic/i }), "130");
    await user.type(screen.getByRole("spinbutton", { name: /diastolic/i }), "85");
    await user.click(screen.getByRole("button", { name: /^submit$/i }));

    await waitFor(() => expect(recordVitals).toHaveBeenCalledWith("enc-77", [
      { type: "BP", value: 130 },
      { type: "BPDiastolic", value: 85 },
    ]));
  });

  it("leads with the same identity block the patient file uses", async () => {
    const user = userEvent.setup();
    const { container } = renderWorkspace(fakeApi());
    // The strip renders nothing until the profile header has answered — a PARTIAL identity would be worse
    // than none on a control whose whole job is confirming which record is open.
    await screen.findByText("Fatma Ibrahim");

    // The SAME component, not a lookalike: the strip used to be a flat dot-separated line of the same
    // fields, so one patient rendered two different ways depending on which screen you were on.
    const identity = container.querySelector(".patient-context-bar .profile-identity");
    expect(identity).not.toBeNull();
    const block = within(identity as HTMLElement);
    expect(block.getByText(/MRS-M-884291/)).toBeInTheDocument();
    expect(block.getByText("Active")).toBeInTheDocument();
    // Icon-per-fact strip, each fact named for a screen reader rather than left as a bare value.
    expect(block.getByText(/^33 yrs$/)).toBeInTheDocument();
    expect(block.getByText("Female")).toBeInTheDocument();

    // And the name still opens the file.
    await user.click(block.getByRole("button", { name: "Fatma Ibrahim" }));
  });

  it("names the patient's allergies rather than counting them", async () => {
    renderWorkspace(fakeApi({
      patientProfile: vi.fn().mockResolvedValue({
        beneficiaryId: "ben-9", servedAt: "2026-08-01T09:00:00Z",
        sections: [
          { key: "header", state: "Visible", variant: "full", data: {
            beneficiaryId: "ben-9", displayName: "Fatma Ibrahim", status: "Active",
            statusCue: { label: "Active", tone: "ok", shape: "circle" },
          } },
          { key: "alerts", state: "Visible", variant: "full", data: {
            allergies: [{ allergen: "Penicillin", severity: "Moderate" }],
          } },
        ],
      }),
    }));

    // This is the screen where a prescription is written. "1 alert" sends the doctor hunting for the one
    // fact that decides what they may prescribe.
    expect(await screen.findByText(/Penicillin/)).toBeInTheDocument();
  });

  it("keeps an unrecorded reading visible as an empty slot rather than dropping its row", async () => {
    const { container } = renderWorkspace(fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({
        vitals: {
          heightCm: null, weightKg: null, systolic: 118, diastolic: 76,
          heartRate: 72, tempC: null, spo2: null, measuredAt: "2026-08-01T09:15:00Z",
        },
      })),
    }));

    await screen.findByText("118 / 76");
    const panel = within(container.querySelector(".vitals-panel") as HTMLElement);
    // "Nobody took this patient's temperature" and "temperature does not apply here" are different facts.
    const temp = panel.getByText("Temperature").closest(".vital-row")!;
    expect(within(temp as HTMLElement).getByText("—")).toBeInTheDocument();
    expect(within(temp as HTMLElement).queryByText(/high|low|in range/i)).not.toBeInTheDocument();
  });

  it("does not read the patient's orders, prescriptions or history until those tabs are opened", async () => {
    const ordersMine = vi.fn().mockResolvedValue([]);
    const prescriptionsMine = vi.fn().mockResolvedValue([]);
    renderWorkspace(fakeApi({ ordersMine, prescriptionsMine }));

    await screen.findByRole("textbox", { name: "Subjective" });
    // Every tab panel stays mounted by design, so without lazily rendering their contents, opening ONE
    // consultation would read three more sets of clinical records — and audit three more PHI accesses —
    // for a doctor who only came to write the note.
    expect(ordersMine).not.toHaveBeenCalled();
    expect(prescriptionsMine).not.toHaveBeenCalled();

    await userEvent.setup().click(screen.getByRole("tab", { name: /orders/i }));
    await waitFor(() => expect(ordersMine).toHaveBeenCalled());
  });

  it("splits the history into encounters, investigations and prescriptions, in one fetch", async () => {
    const user = userEvent.setup();
    const patientProfile = vi.fn(async (_id: string, sections?: readonly string[]) => ({
      beneficiaryId: "ben-9", servedAt: "2026-08-01T09:00:00Z",
      sections: sections?.includes("encounters")
        ? [
            { key: "encounters", state: "Visible", data: { items: [
              { encounterRef: "ENC-2026-1", occurredAt: "2026-07-02T09:00:00Z", status: "Completed" }] } },
            { key: "investigations", state: "Visible", data: { items: [
              { orderRef: "ORD-1", lineId: "l1", category: "Haematology",
                orderedOn: "2026-07-02T09:20:00Z", status: "Resulted" }] } },
            { key: "prescriptions", state: "Visible", data: { items: [
              { rxRef: "RX-1", drugDisplay: "Amoxicillin 500mg", status: "Dispensed",
                prescribedOn: "2026-07-02T09:30:00Z" }] } },
          ]
        : [{ key: "header", state: "Visible", data: {
              beneficiaryId: "ben-9", displayName: "Fatma Ibrahim", status: "Active",
              statusCue: { label: "Active", tone: "ok", shape: "circle" } } }],
    }));
    renderWorkspace(fakeApi({ patientProfile: patientProfile as never }));

    await user.click(await screen.findByRole("tab", { name: /history/i }));
    expect(await screen.findByText("ENC-2026-1")).toBeInTheDocument();

    // All three came back in ONE call — a tab per round trip would be three audited PHI reads for one
    // question a doctor asks in one breath.
    const historyCall = patientProfile.mock.calls.find((c) => c[1]?.includes("encounters"));
    expect(historyCall?.[1]).toEqual(["encounters", "investigations", "prescriptions"]);

    await user.click(screen.getByRole("tab", { name: "Investigations" }));
    expect(await screen.findByText("ORD-1")).toBeInTheDocument();
    await user.click(screen.getByRole("tab", { name: "Prescriptions" }));
    expect(await screen.findByText("Amoxicillin 500mg")).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderWorkspace(fakeApi());
    await screen.findByRole("textbox", { name: "Subjective" });
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
