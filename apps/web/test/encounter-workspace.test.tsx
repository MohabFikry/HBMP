import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
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
    completeEncounter: vi.fn().mockResolvedValue(undefined),
    addEncounterDiagnosis: vi.fn(),
    removeEncounterDiagnosis: vi.fn().mockResolvedValue(undefined),
    searchIcd: vi.fn().mockResolvedValue([]),
    listPatients: vi.fn().mockResolvedValue([]),
    ordersMine: vi.fn().mockResolvedValue([]),
    prescriptionsMine: vi.fn().mockResolvedValue([]),
    recordVitals: vi.fn().mockResolvedValue({ encounterId: "enc-77", recorded: 1 }),
    // MemberClinicalPanel reads the member's standing clinical facts on open, directly beneath the context
    // strip. Empty is the honest default: nothing recorded, which the panel says in so many words.
    memberClinicalRecord: vi.fn().mockResolvedValue({
      beneficiaryId: "ben-9", bloodGroup: null, bloodGroupRecordedAt: null, allergies: [],
    }),
    allergenCatalogue: vi.fn().mockResolvedValue([]),
    addAllergy: vi.fn(),
    setBloodGroup: vi.fn().mockResolvedValue(undefined),
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
    // 30.6 — the amend/cancel picker. Present because the detail dialogs now ask for it; a partial
    // fake that omits it is a fake that has drifted from the interface it claims to satisfy.
    amendmentReasons: async () => [
      { code: "ClinicalChange", nameEn: "Clinical change", nameAr: "تغير الحالة السريرية" },
    ],
    cancelOrderLine: async () => {},
    amendOrderLine: async () => {},
    cancelPrescriptionLine: async () => {},
    amendPrescriptionLine: async () => {},
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

    // Irreversible, so it is confirmed — and nothing has been signed yet at this point. The prompt names
    // both consequences: the note locks AND the appointment leaves the day list.
    expect(await screen.findByText(/signs the note and closes the visit/i)).toBeInTheDocument();
    expect(signEncounterNote).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: /sign & close visit/i }));
    await waitFor(() => expect(signEncounterNote).toHaveBeenCalledWith("enc-77", "note-1"));
    // Saved BEFORE signing: signing a note that was never written would lock an empty record.
    expect(saveEncounterNote).toHaveBeenCalled();
    expect(saveEncounterNote.mock.invocationCallOrder[0])
      .toBeLessThan(signEncounterNote.mock.invocationCallOrder[0]);
  });

  it("closes the VISIT as well as signing the note", async () => {
    const user = userEvent.setup();
    const completeEncounter = vi.fn().mockResolvedValue(undefined);
    const signEncounterNote = vi.fn().mockResolvedValue(undefined);
    renderWorkspace(fakeApi({
      completeEncounter, signEncounterNote,
      getEncounter: vi.fn().mockResolvedValue(encounter({
        diagnoses: [{ id: "dx-1", system: "ICD-10", code: "J01.90", rank: "Primary",
                      label: { en: "Acute sinusitis", ar: "التهاب جيوب" } }],
      })),
    }));

    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Supportive care");
    await user.click(screen.getByRole("button", { name: /save & finalize/i }));
    await user.click(await screen.findByRole("button", { name: /sign & close visit/i }));

    // Signing a note is documentation; closing the visit is what moves the appointment to Completed and
    // takes "Start visit" off the doctor's day list. Doing only the first is the defect this covers.
    await waitFor(() => expect(completeEncounter).toHaveBeenCalledWith("enc-77"));
    expect(signEncounterNote.mock.invocationCallOrder[0])
      .toBeLessThan(completeEncounter.mock.invocationCallOrder[0]);
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

    // Each of the three now fetches only its own list. Opening Prescriptions must not pull the lab and
    // imaging worklist, and opening Labs must not pull the prescriptions.
    const user = userEvent.setup();
    await user.click(screen.getByRole("tab", { name: "Prescriptions" }));
    await waitFor(() => expect(prescriptionsMine).toHaveBeenCalled());
    expect(ordersMine).not.toHaveBeenCalled();

    await user.click(screen.getByRole("tab", { name: "Labs" }));
    await waitFor(() => expect(ordersMine).toHaveBeenCalled());
  });

  /** One prescription for ben-9, with a fully-recorded line and one written before the name snapshot. */
  function rxRow(over: Record<string, unknown> = {}) {
    return {
      id: "47f2a33f-d49d-4bbf-97b1-1bb8b35287af",
      rxNo: "RX-2026-000312",
      beneficiary: { id: "ben-9", token: "•••4821" },
      lineCount: 2,
      status: { kind: "ok", label: { en: "Approved", ar: "معتمدة" } },
      submittedAt: "2026-08-03T09:40:00Z",
      expiresAt: undefined,
      prescriber: { en: "Dr Karim Abdel-Latif", ar: "د. كريم عبد اللطيف" },
      lines: [
        {
          id: "l1", drug: { en: "Augmentin 600mg vial", ar: "أوجمنتين 600مجم" },
          dose: "1 g", route: "PO", frequency: "BD",
          quantityPrescribed: 14, quantityDispensed: 0, refillsAllowed: 0,
          status: { kind: "info", label: { en: "Active", ar: "نشطة" } },
        },
        {
          id: "l2", drug: null, dose: null, route: "PO", frequency: "OD",
          quantityPrescribed: 30, quantityDispensed: 0, refillsAllowed: 1,
          status: { kind: "info", label: { en: "Active", ar: "نشطة" } },
        },
      ],
      ...over,
    };
  }

  it("references a prescription by its Rx number, never by the internal id", async () => {
    const prescriptionsMine = vi.fn().mockResolvedValue([rxRow()]);
    renderWorkspace(fakeApi({ prescriptionsMine }));

    await userEvent.setup().click(await screen.findByRole("tab", { name: "Prescriptions" }));

    // The uuid was printed under a heading that says "Reference", which makes it read as one. It is not:
    // the pharmacy, the patient's paper copy and every phone call use RX-2026-000312.
    expect(await screen.findByText("RX-2026-000312")).toBeInTheDocument();
    expect(screen.queryByText("47f2a33f-d49d-4bbf-97b1-1bb8b35287af")).toBeNull();
  });

  it("opens the prescription as written, without a second fetch", async () => {
    const user = userEvent.setup();
    const prescriptionsMine = vi.fn().mockResolvedValue([rxRow()]);
    renderWorkspace(fakeApi({ prescriptionsMine }));

    await user.click(await screen.findByRole("tab", { name: "Prescriptions" }));
    await user.click(await screen.findByRole("button", { name: "View prescription RX-2026-000312" }));

    const dialog = within(await screen.findByRole("dialog"));
    // The sig is what makes this worth opening: the table above says "2 lines", not what they were.
    expect(dialog.getByText("Augmentin 600mg vial")).toBeInTheDocument();
    expect(dialog.getByText("1 g")).toBeInTheDocument();
    expect(dialog.getByText("BD")).toBeInTheDocument();
    expect(dialog.getByText("Dr Karim Abdel-Latif")).toBeInTheDocument();

    // Everything came from the row already on screen. A dialog that re-fetched would add one audited PHI
    // read per glance to this patient's trail.
    expect(prescriptionsMine).toHaveBeenCalledTimes(1);
  });

  it("says a medication was not recorded rather than printing the word 'Medication'", async () => {
    const user = userEvent.setup();
    renderWorkspace(fakeApi({ prescriptionsMine: vi.fn().mockResolvedValue([rxRow()]) }));

    await user.click(await screen.findByRole("tab", { name: "Prescriptions" }));
    await user.click(await screen.findByRole("button", { name: "View prescription RX-2026-000312" }));

    const dialog = within(await screen.findByRole("dialog"));
    // Line 2 predates the drug-name snapshot. The name of the field where its value belongs reads as data,
    // and nobody downstream can tell it apart from one — so the gap is named as a gap.
    expect(dialog.getByText("Medication not recorded")).toBeInTheDocument();
    expect(dialog.queryByText(/^Medication$/)).toBeNull();
  });

  it("shows what was prescribed apart from what has been dispensed", async () => {
    const user = userEvent.setup();
    const rx = rxRow({
      lines: [{
        id: "l1", drug: { en: "Metformin 500mg", ar: "ميتفورمين" },
        dose: "500 mg", route: "PO", frequency: "BD",
        quantityPrescribed: 60, quantityDispensed: 30, refillsAllowed: 2,
        status: { kind: "part", label: { en: "Partially dispensed", ar: "صُرفت جزئياً" } },
      }],
    });
    renderWorkspace(fakeApi({ prescriptionsMine: vi.fn().mockResolvedValue([rx]) }));

    await user.click(await screen.findByRole("tab", { name: "Prescriptions" }));
    await user.click(await screen.findByRole("button", { name: "View prescription RX-2026-000312" }));

    // 60 written, 30 handed over. Never folded into a single "30 remaining": this dialog answers what was
    // PRESCRIBED, and a reader checking their own dose against it must see the figure they wrote.
    const dialog = within(await screen.findByRole("dialog"));
    const prescribed = dialog.getByText("Quantity prescribed").closest(".rxv-cell")!;
    expect(within(prescribed as HTMLElement).getByText("60")).toBeInTheDocument();
    const dispensed = dialog.getByText("Dispensed to date").closest(".rxv-cell")!;
    expect(within(dispensed as HTMLElement).getByText("30")).toBeInTheDocument();
  });

  it("names each row's view button by its own prescription", async () => {
    const user = userEvent.setup();
    const rows = [rxRow(), rxRow({ id: "rx-b", rxNo: "RX-2026-000275" })];
    renderWorkspace(fakeApi({ prescriptionsMine: vi.fn().mockResolvedValue(rows) }));

    await user.click(await screen.findByRole("tab", { name: "Prescriptions" }));
    // Three identically-named buttons in a column is a screen reader hearing "View prescription" three
    // times with nothing to say which row it is on.
    expect(await screen.findByRole("button", { name: "View prescription RX-2026-000312" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "View prescription RX-2026-000275" })).toBeInTheDocument();
  });

  it("has no a11y violations with a prescription open", async () => {
    const user = userEvent.setup();
    const { container } = renderWorkspace(fakeApi({ prescriptionsMine: vi.fn().mockResolvedValue([rxRow()]) }));

    await user.click(await screen.findByRole("tab", { name: "Prescriptions" }));
    await user.click(await screen.findByRole("button", { name: "View prescription RX-2026-000312" }));
    await screen.findByRole("dialog");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
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

    const historyTabs = within(screen.getByRole("tabpanel", { name: /history/i }));
    await user.click(historyTabs.getByRole("tab", { name: "Investigations" }));
    expect(await screen.findByText("ORD-1")).toBeInTheDocument();
    await user.click(historyTabs.getByRole("tab", { name: "Prescriptions" }));
    expect(await screen.findByText("Amoxicillin 500mg")).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderWorkspace(fakeApi());
    await screen.findByRole("textbox", { name: "Subjective" });
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

/**
 * Closing a visit over work that was composed and never sent.
 *
 * ============================================================================================================
 * WHY THIS IS A GATE AND NOT A NUDGE
 * ============================================================================================================
 * Prescribing is not required to finish a consultation — plenty of visits end without one, and the rule here
 * is emphatically NOT "you must prescribe". But a prescription that was composed, checked, and had its
 * warnings answered in writing, and then never sent, is not a decision not to prescribe. It is a decision that
 * was made and lost: the doctor believes the patient is collecting medicine, the pharmacy has never heard of
 * it, and the encounter is now signed and locked, so the record of the visit says nothing was prescribed at
 * all. Nobody finds out until the patient does.
 *
 * So: send it, or discard it. Both are one click.
 */
describe("unsent work blocks the close", () => {
  /** A client that can compose and check a prescription, on top of the encounter fixture. */
  function prescribingApi() {
    return fakeApi({
      getEncounter: vi.fn().mockResolvedValue(encounter({
        diagnoses: [{ id: "dx-1", system: "ICD-10", code: "J01.90", rank: "Primary",
                      label: { en: "Acute sinusitis, unspecified", ar: "التهاب جيوب حاد" } }],
      })),
      searchPrescribableDrugs: vi.fn().mockResolvedValue([
        {
          drugId: "d-1",
          tradeName: { en: "Augmentin 1g", ar: "أوجمنتين ١ جم" },
          activeIngredient: "amoxicillin + clavulanic acid",
          strength: "1g", form: "Tablet", priceEgp: 90, hasIndicationData: true,
        },
      ]),
      validatePrescription: vi.fn().mockResolvedValue({
        validationId: "v-1", overallState: "Ok", findings: [], lineStates: {},
      }),
      searchCpt: vi.fn().mockResolvedValue([
        { code: "85025", description: "Blood count; complete (CBC), automated" },
      ]),
    });
  }

  /** Compose one prescription line in the Prescriptions tab and come back to the note. */
  async function composePrescription(user: ReturnType<typeof userEvent.setup>) {
    await user.click(await screen.findByRole("tab", { name: /prescriptions/i }));
    // BY ITS LABEL. A bare `combobox` role also matches every `<select>` on the screen — the vitals rail has
    // several — and typing into one of those silently does nothing.
    const box = await screen.findByRole("combobox", { name: "Medicine" });
    await user.type(box, "augmentin");
    await user.click((await screen.findAllByRole("option"))[0]);
    await screen.findByText(/Augmentin/);
    await user.click(screen.getByRole("tab", { name: /soap note/i }));
  }

  it("refuses to finalize while a composed prescription has not been sent, and names the tab", async () => {
    const user = userEvent.setup();
    const api = prescribingApi();
    renderWorkspace(api);

    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Amoxicillin, five days");
    // Everything else is in order — primary diagnosis recorded, note written — so the button is live.
    expect(screen.getByRole("button", { name: /save & finalize/i })).toBeEnabled();

    await composePrescription(user);

    // The composer is in a tab the doctor is no longer looking at, which is exactly why the reason has to
    // name it rather than say "unsent work".
    await waitFor(() => expect(screen.getByRole("button", { name: /save & finalize/i })).toBeDisabled());
    expect(screen.getByText(/Composed but not sent: Prescriptions/)).toBeInTheDocument();
    expect(api.signEncounterNote).not.toHaveBeenCalled();
  });

  it("lets the visit close once the composed prescription is discarded", async () => {
    const user = userEvent.setup();
    const api = prescribingApi();
    renderWorkspace(api);

    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Supportive care only");
    await composePrescription(user);
    await waitFor(() => expect(screen.getByRole("button", { name: /save & finalize/i })).toBeDisabled());

    // Discard is the half of the rule that makes it fair: the gate can only insist on "sent or discarded" if
    // discarding is something the screen actually offers.
    await user.click(screen.getByRole("tab", { name: /prescriptions/i }));
    await user.click(screen.getByRole("button", { name: /^discard$/i }));
    const confirm = await screen.findByRole("dialog");
    await user.click(within(confirm).getByRole("button", { name: /^discard$/i }));

    await user.click(screen.getByRole("tab", { name: /soap note/i }));
    await waitFor(() => expect(screen.getByRole("button", { name: /save & finalize/i })).toBeEnabled());
    expect(screen.queryByText(/Composed but not sent/)).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /save & finalize/i }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: /sign & close visit/i }));
    await waitFor(() => expect(api.signEncounterNote).toHaveBeenCalled());
  });

  it("applies the same rule to a composed lab order, and names Labs", async () => {
    const user = userEvent.setup();
    const api = prescribingApi();
    renderWorkspace(api);

    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Bloods today");
    expect(screen.getByRole("button", { name: /save & finalize/i })).toBeEnabled();

    // Three composers feed this gate and each is wired separately — a right rule pointed at the wrong key
    // reports clean forever, which is the one way a safety check fails without anyone noticing.
    await user.click(screen.getByRole("tab", { name: /^labs$/i }));
    await user.type(await screen.findByRole("combobox", { name: "Test" }), "cbc");
    await user.click((await screen.findAllByRole("option"))[0]);
    await screen.findByText(/CPT 85025/);
    await user.click(screen.getByRole("tab", { name: /soap note/i }));

    await waitFor(() => expect(screen.getByRole("button", { name: /save & finalize/i })).toBeDisabled());
    expect(screen.getByText(/Composed but not sent: Labs/)).toBeInTheDocument();
  });

  it("does not ask for a prescription that was never started", async () => {
    const user = userEvent.setup();
    const api = prescribingApi();
    renderWorkspace(api);

    // The rule is "do not leave one half-done", never "you must prescribe". A visit that ends without one
    // closes exactly as it did before any of this existed.
    await user.type(await screen.findByRole("textbox", { name: "Plan" }), "Reassurance, review if worse");
    expect(screen.getByRole("button", { name: /save & finalize/i })).toBeEnabled();
    expect(screen.queryByText(/Composed but not sent/)).not.toBeInTheDocument();
  });
});
