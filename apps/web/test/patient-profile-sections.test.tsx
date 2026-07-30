import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { PatientProfile } from "../src/screens/PatientProfile";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import type { PatientProfile as PatientProfileContract } from "@mersal/contracts";
import { ApiProvider } from "../src/api/ApiProvider";

/**
 * Phase 20.4 — the designed views for sections 3–14 (design 39 §3).
 *
 * <b>The property under test is that data the server served actually reaches the screen.</b> These sections
 * previously went through a generic key/value renderer that filtered out every value whose `typeof` was
 * "object" — so coverage's limits, a history's conditions and a case's task list were dropped, and a payload
 * that was entirely nested rendered as "No records". That is not a cosmetic defect: it made real data
 * indistinguishable from `NotApplicable`, which design 39 §6 forbids precisely because a clinician who reads
 * "no records" believes something untrue about a patient.
 *
 * So the assertions below are mostly of one shape: <i>this nested thing is on screen, and the empty label is
 * not</i>. The rest cover the projection contract — an absent field renders as nothing rather than as a blank
 * cell — and the bilingual requirement, since the old renderer printed raw camelCase keys as labels.
 */

const BEN = "b-amal";

function profile(sections: PatientProfileContract["sections"]): PatientProfileContract {
  return { beneficiaryId: BEN, servedAt: new Date().toISOString(), sections };
}

function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  const dev = new DevApiClient({ latencyMs: 0 });
  return Object.assign(dev, over) as ApiClient;
}

/** Render the profile with exactly the sections a test cares about. */
function renderSections(sections: PatientProfileContract["sections"]) {
  const api = fakeApi({ patientProfile: vi.fn().mockResolvedValue(profile(sections)) });
  return renderNode(
    <ApiProvider client={api}>
      <PatientProfile beneficiaryId={BEN} />
    </ApiProvider>,
  );
}

function visible(key: string, data: unknown): PatientProfileContract["sections"][number] {
  return { key, state: "Visible", data };
}

/** The language is read from localStorage at ThemeProvider mount, so it must be set BEFORE render. */
function useArabic() {
  localStorage.setItem("mersal-lang", "ar");
}

afterEach(() => {
  localStorage.clear();
});

// ---------------------------------------------------------------- the regression that started this

describe("20.4 — nested payloads are rendered, not silently dropped", () => {
  it("renders coverage's per-category limits, and does not call the section empty", async () => {
    // The exact shape that failed: scalars printed, `categories` discarded for being an array. A receptionist
    // saw the payer and the plan and no limits at all — which is the one question they are asked all day.
    renderSections([
      visible("coverage", {
        payerName: "Mersal Foundation",
        policyNo: "POL-2026-0001",
        planLabel: "Gold",
        planVersion: 3,
        categories: [
          { category: "Pharmacy", annualLimit: 5000, consumed: 1200, remaining: 3800, costSharePercent: 10, costShareTier: "Tier1" },
          { category: "Dental", annualLimit: 2000, consumed: 0, remaining: 2000, costSharePercent: 20 },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /coverage/i });
    expect(within(section).getByText("Pharmacy")).toBeInTheDocument();
    expect(within(section).getByText("Dental")).toBeInTheDocument();
    // The number a member is actually told, formatted as money rather than a bare integer.
    expect(within(section).getByText(/3,800/)).toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });

  it("renders a history whose only content is nested — the shape that read as empty", async () => {
    // `V(summary)`, the case manager's projection: narrative and uploadedRecords are nulled server-side, so
    // `conditions` is the whole payload. Every value is an object, so the old renderer had nothing left to
    // print and reported "No records" over a patient with two active chronic conditions.
    renderSections([
      visible("pastMedicalHistory", {
        conditions: [
          { system: "ICD-10", code: "E11.9", display: "Type 2 diabetes mellitus", clinicalStatus: "Active" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /past medical history/i });
    expect(within(section).getByText("Type 2 diabetes mellitus")).toBeInTheDocument();
    expect(within(section).getByText(/E11\.9/)).toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });

  it("renders case management's three lists — a payload with no scalar field at all", async () => {
    // Cases, tasks and escalations are three sibling arrays and there is no scalar anywhere in the section,
    // so the generic renderer reported "No records" for every beneficiary who had a case open.
    renderSections([
      visible("caseManagement", {
        cases: [{ caseId: "c1", caseNo: "CASE-2026-0217", status: "Open", category: "ChronicCare", openedAt: "2026-05-04T08:00:00Z" }],
        tasks: [{ taskId: "t1", title: "Confirm endocrinology follow-up", status: "Open", dueOn: "2026-07-10" }],
        escalations: [{ escalationId: "e1", reason: "Insulin out of stock", status: "Escalated", raisedAt: "2026-07-26T15:00:00Z" }],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /case management/i });
    expect(within(section).getByText("CASE-2026-0217")).toBeInTheDocument();
    expect(within(section).getByText("Confirm endocrinology follow-up")).toBeInTheDocument();
    expect(within(section).getByText("Insulin out of stock")).toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });

  it("renders the financial claims ledger", async () => {
    renderSections([
      visible("financial", {
        currency: "EGP",
        costShareOwed: 420,
        settlementStatus: "Pending",
        claims: [{ claimNo: "CLM-2026-3391", serviceDate: "2026-07-02", billedAmount: 1800, approvedAmount: 1620, memberShare: 180, status: "Settled" }],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /financial/i });
    expect(within(section).getByText("CLM-2026-3391")).toBeInTheDocument();
    expect(within(section).getByText(/1,800/)).toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });

  it("still says No records when the payload genuinely holds nothing", async () => {
    // The other half of the property: the fix must not make emptiness unsayable. An empty list is a calm,
    // ordinary fact and it must still read as one.
    renderSections([visible("prescriptions", { items: [] })]);

    const section = await screen.findByRole("region", { name: /prescriptions/i });
    expect(within(section).getByText(/no records/i)).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- absence is not a blank

describe("20.4 — a field the projection dropped renders as nothing, not as an empty cell", () => {
  it("omits the Reason column entirely for a meta-projected encounter list", async () => {
    // `V(meta)` for reception, finance and beneficiary management: the visit's logistics without its clinical
    // content. A Reason column standing empty down its whole length says "no reason was recorded", which is a
    // different and untrue statement about the doctor who saw this patient.
    renderSections([
      visible("encounters", {
        items: [
          { encounterRef: "ENC-2026-1", occurredAt: "2026-07-02T09:00:00Z", branchName: "Nasr City", status: "Completed" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /encounters/i });
    expect(within(section).getByText("ENC-2026-1")).toBeInTheDocument();
    expect(within(section).queryByRole("columnheader", { name: /reason/i })).not.toBeInTheDocument();
  });

  it("shows the Reason column when the clinical projection carries one", async () => {
    renderSections([
      visible("encounters", {
        items: [
          { encounterRef: "ENC-2026-1", occurredAt: "2026-07-02T09:00:00Z", reason: "Follow-up, diabetes", status: "Completed" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /encounters/i });
    expect(within(section).getByRole("columnheader", { name: /reason/i })).toBeInTheDocument();
    expect(within(section).getByText("Follow-up, diabetes")).toBeInTheDocument();
  });

  it("omits rationale and amount columns for a reception-projected authorization list", async () => {
    // `V(status)`: reception tells a member "approved until the 30th" and is shown neither the clinical
    // reasoning nor the money.
    renderSections([
      visible("authorizations", {
        items: [{ authNo: "AUTH-1", status: "Approved", requestedAt: "2026-07-20T10:00:00Z", validUntil: "2026-08-30" }],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /authorizations/i });
    expect(within(section).getByText("AUTH-1")).toBeInTheDocument();
    expect(within(section).queryByRole("columnheader", { name: /rationale/i })).not.toBeInTheDocument();
    expect(within(section).queryByRole("columnheader", { name: /approved amount/i })).not.toBeInTheDocument();
  });

  it("renders financial headline facts with no claims table under the summary projection", async () => {
    renderSections([visible("financial", { currency: "EGP", costShareOwed: 420, settlementStatus: "Pending" })]);

    const section = await screen.findByRole("region", { name: /financial/i });
    expect(within(section).getByText(/cost share owed/i)).toBeInTheDocument();
    expect(within(section).queryByRole("table")).not.toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- the gates that must survive rendering

describe("20.4 — row-level gates are rendered as gates", () => {
  it("marks a sensitivity-restricted result as restricted and shows no value", async () => {
    renderSections([
      visible("investigations", {
        items: [
          { orderRef: "ORD-1", lineId: "l1", category: "Serology", orderedOn: "2026-07-20T10:00:00Z", status: "Resulted", restricted: true, sensitivityLevel: "High" },
          { orderRef: "ORD-2", lineId: "l2", category: "Haematology", orderedOn: "2026-07-21T10:00:00Z", status: "Resulted", resultSummary: "Hb 11.2 g/dL" },
          { orderRef: "ORD-3", lineId: "l3", category: "Chemistry", orderedOn: "2026-07-22T10:00:00Z", status: "Ordered" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /investigations/i });
    // Restricted: said in words, with its sensitivity level.
    expect(within(section).getByText(/sensitivity-restricted/i)).toBeInTheDocument();
    expect(within(section).getByText("High")).toBeInTheDocument();
    // The unrestricted result is shown.
    expect(within(section).getByText("Hb 11.2 g/dL")).toBeInTheDocument();
    // And a genuine wait is distinguishable from a locked door — a clinician who confuses the two waits for a
    // result that will never arrive without a request-access grant.
    expect(within(section).getByText(/awaiting result/i)).toBeInTheDocument();
  });

  it("offers no download control for a document whose content is gated", async () => {
    // Metadata always, content separately gated (design 39 §3 row 10). Absent rather than disabled: a greyed
    // download advertises a file this caller will never open.
    renderSections([
      visible("documents", {
        items: [
          { linkId: "d1", title: "UNHCR registration card", uploadedAt: "2025-01-13T09:00:00Z", status: "Verified", mayDownload: true },
          { linkId: "d2", title: "Radiology report — lumbar MRI", uploadedAt: "2026-07-22T16:40:00Z", status: "Active", mayDownload: false },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /documents/i });
    // Both rows exist — the gated one is not hidden, only its content is.
    expect(within(section).getByText("Radiology report — lumbar MRI")).toBeInTheDocument();
    // The accessible name identifies the file: "Download" repeated down a column names nothing.
    expect(within(section).getByRole("button", { name: /download — UNHCR registration card/i })).toBeInTheDocument();
    expect(within(section).queryByRole("button", { name: /radiology report/i })).not.toBeInTheDocument();
  });

  it("shows a withheld note's existence and none of its content", async () => {
    renderSections([
      visible("notes", {
        items: [
          { noteId: "n1", noteType: "Coordination", body: "Prefers afternoon appointments.", authorDisplay: "H. Mostafa", createdAt: "2026-07-15T10:05:00Z", withheld: false, pinned: true },
          { noteId: "n2", noteType: "Clinical", authorDisplay: "Dr. S. Ibrahim", createdAt: "2026-07-22T13:20:00Z", withheld: true },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /^notes$/i });
    expect(within(section).getByText("Prefers afternoon appointments.")).toBeInTheDocument();
    // The withheld note is present, named and dated — so a user requests access rather than assuming nothing
    // was written. Its body is not there to be withheld: the server never sent one.
    expect(within(section).getByText(/this note exists/i)).toBeInTheDocument();
    expect(within(section).getByText("Dr. S. Ibrahim")).toBeInTheDocument();
  });

  it("states a referral loop as open or closed rather than leaving a timestamp to interpret", async () => {
    renderSections([
      visible("referrals", {
        items: [
          { referralRef: "REF-1", status: "Active", requestedSpecialty: "Cardiology", createdAt: "2026-07-01T10:00:00Z" },
          { referralRef: "REF-2", status: "Completed", requestedSpecialty: "Ophthalmology", createdAt: "2026-06-01T10:00:00Z", loopClosedAt: "2026-06-20T10:00:00Z" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /referrals/i });
    expect(within(section).getByText("Open")).toBeInTheDocument();
    expect(within(section).getByText("Closed")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- bilingual, and no JSON on screen

describe("20.4 — labels are translated, never raw payload keys", () => {
  it("prints no camelCase field name anywhere in a fully populated profile", async () => {
    const { container } = renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /coverage/i });

    // The old renderer used `Object.entries` keys as <dt> text and as row labels. These are the field names it
    // would have printed; none of them is a word any user should ever read.
    for (const key of [
      "payerName", "policyNo", "planLabel", "annualLimit", "costSharePercent", "costShareTier",
      "clinicalStatus", "onsetOn", "encounterRef", "occurredAt", "drugDisplay", "prescribedOn",
      "authNo", "requestedAt", "validUntil", "approvedAmount", "referralRef", "loopClosedAt",
      "visibilityClass", "mayDownload", "authorDisplay", "costShareOwed", "settlementStatus",
      "claimNo", "billedAmount", "memberShare", "caseNo", "openedAt", "escalationId", "eventType",
      "sourceService", "actorDisplay",
    ]) {
      expect(container.textContent).not.toContain(key);
    }
  });

  it("renders Arabic labels in Arabic", async () => {
    useArabic();
    renderSections([
      visible("coverage", {
        payerName: "Mersal Foundation",
        categories: [{ category: "Pharmacy", annualLimit: 5000, remaining: 3800 }],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /التغطية/ });
    // The fact label and a column header, both from the bilingual table rather than the payload.
    expect(within(section).getByText("الجهة الممولة")).toBeInTheDocument();
    expect(within(section).getByRole("columnheader", { name: /المتبقي/ })).toBeInTheDocument();
  });

  it("translates a known status into Arabic and passes an unknown one through", async () => {
    // A lexicon that swallowed what it did not recognise would turn a newly added state into a blank chip.
    // Passing the raw token through is visibly imperfect, which is the right failure mode for a missing word.
    useArabic();
    renderSections([
      visible("referrals", {
        items: [
          { referralRef: "REF-1", status: "Active", createdAt: "2026-07-01T10:00:00Z" },
          { referralRef: "REF-2", status: "SomeBrandNewState", createdAt: "2026-07-02T10:00:00Z" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /الإحالات/ });
    expect(within(section).getByText("نشط")).toBeInTheDocument();
    expect(within(section).getByText("SomeBrandNewState")).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- unknown sections

describe("20.4 — an unknown section key is shown, not reported as empty", () => {
  it("surfaces nested content from a section this client does not know", async () => {
    // A server ahead of this client. The honest failure is an ugly render; the dishonest one is "No records".
    renderSections([visible("someFutureSection", { headline: "Something new", rows: [{ id: 1, label: "kept" }] })]);

    const section = await screen.findByRole("region", { name: /someFutureSection/i });
    expect(within(section).getByText(/something new/i)).toBeInTheDocument();
    // The nested array survives rather than being filtered away for being an object.
    expect(within(section).getByText(/kept/)).toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- accessibility

describe("20.4 — accessibility of the new views", () => {
  it("is axe clean with all twelve sections visible, in English", async () => {
    const { container } = renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /timeline/i });

    const results = await axe(container);
    expect(results).toHaveNoViolations();
  });

  it("is axe clean in Arabic RTL", async () => {
    useArabic();
    const { container } = renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /السجل الزمني/ });

    const results = await axe(container);
    expect(results).toHaveNoViolations();
  });

  it("gives every section table an accessible caption", async () => {
    // DataTable renders its caption sr-only. A table with no caption is a table a screen-reader user lands in
    // with no idea which section they are reading — and this screen stacks a dozen of them.
    renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /timeline/i });

    for (const table of screen.getAllByRole("table")) {
      expect(table.querySelector("caption")?.textContent?.trim()).toBeTruthy();
    }
  });
});

// ---------------------------------------------------------------- ordering

describe("20.4 — timeline order", () => {
  it("puts the newest event first, matching every other timeline in the app", async () => {
    renderSections([
      visible("timeline", {
        items: [
          { at: "2026-07-02T11:40:00Z", eventType: "PrescriptionDispensed", sourceService: "pharmacy" },
          { at: "2026-07-26T09:12:00Z", eventType: "ProfileOpened", sourceService: "profile" },
          { at: "2026-07-21T08:30:00Z", eventType: "AuthorizationDecided", sourceService: "approvals" },
        ],
      }),
    ]);

    const section = await screen.findByRole("region", { name: /timeline/i });
    const rows = within(section).getAllByRole("row").slice(1); // drop the header row
    expect(rows[0]).toHaveTextContent("ProfileOpened");
    expect(rows[2]).toHaveTextContent("PrescriptionDispensed");
  });
});

// ---------------------------------------------------------------- shared fixture

/** All twelve designed sections, populated — the shape the a11y and no-raw-keys sweeps need. */
const FULL_PROFILE: PatientProfileContract["sections"] = [
  visible("coverage", {
    payerName: "Mersal Foundation", policyNo: "POL-2026-0001", planLabel: "Gold", planVersion: 3,
    effectiveFrom: "2026-01-01", effectiveTo: "2026-12-31", waitingPeriodState: "Served",
    categories: [
      { category: "Pharmacy", annualLimit: 5000, consumed: 1200, remaining: 3800, costSharePercent: 10, costShareTier: "Tier1" },
      { category: "Dental", annualLimit: 2000, consumed: 1900, remaining: 100, costSharePercent: 20, costShareTier: "Tier2" },
    ],
  }),
  visible("pastMedicalHistory", {
    conditions: [{ system: "ICD-10", code: "E11.9", display: "Type 2 diabetes mellitus", clinicalStatus: "Active", onsetOn: "2021-03-14" }],
    narrative: "Managed on metformin since 2021.",
    uploadedRecords: [{ linkId: "h1", documentClass: "Clinical", title: "Discharge summary", documentDate: "2024-08-19" }],
  }),
  visible("encounters", {
    items: [{ encounterRef: "ENC-2026-1", occurredAt: "2026-07-02T09:00:00Z", branchName: "Nasr City", clinicianName: "Dr. S. Ibrahim", specialty: "Internal medicine", reason: "Follow-up", status: "Completed" }],
  }),
  visible("investigations", {
    items: [{ orderRef: "ORD-1", lineId: "l1", category: "Haematology", orderedOn: "2026-07-20T10:00:00Z", status: "Resulted", providerName: "Central Lab", resultSummary: "Hb 11.2 g/dL" }],
  }),
  visible("prescriptions", {
    items: [{ rxRef: "RX-1", drugDisplay: "Metformin 850mg", status: "Dispensed", prescribedOn: "2026-07-02T09:10:00Z", dispensedOn: "2026-07-02T11:40:00Z", batchNo: "MTF-2291", expiryDate: "2027-04-30" }],
  }),
  visible("authorizations", {
    items: [{ authNo: "AUTH-1", serviceCategory: "Imaging", status: "Approved", requestedAt: "2026-07-20T10:00:00Z", decidedAt: "2026-07-21T08:30:00Z", validUntil: "2026-08-30", rationale: "MRI indicated.", approvedAmount: 3200 }],
  }),
  visible("referrals", {
    items: [{ referralRef: "REF-1", status: "Active", requestedSpecialty: "Cardiology", createdAt: "2026-07-01T10:00:00Z" }],
  }),
  visible("documents", {
    items: [{ linkId: "d1", documentClass: "Identity", visibilityClass: "Administrative", title: "UNHCR card", documentDate: "2025-01-12", uploadedAt: "2025-01-13T09:00:00Z", status: "Verified", mayDownload: true }],
  }),
  visible("notes", {
    items: [{ noteId: "n1", noteType: "Coordination", visibilityClass: "Administrative", body: "Prefers afternoons.", authorDisplay: "H. Mostafa", createdAt: "2026-07-15T10:05:00Z", pinned: true }],
  }),
  visible("financial", {
    currency: "EGP", costShareOwed: 420, settlementStatus: "Pending",
    claims: [{ claimNo: "CLM-1", serviceDate: "2026-07-02", billedAmount: 1800, approvedAmount: 1620, memberShare: 180, status: "Settled" }],
  }),
  visible("caseManagement", {
    cases: [{ caseId: "c1", caseNo: "CASE-1", status: "Open", category: "ChronicCare", openedAt: "2026-05-04T08:00:00Z" }],
    tasks: [{ taskId: "t1", title: "Confirm follow-up", status: "Open", dueOn: "2026-07-10" }],
    escalations: [{ escalationId: "e1", reason: "Stock-out", status: "Escalated", raisedAt: "2026-07-26T15:00:00Z" }],
  }),
  visible("timeline", {
    items: [{ at: "2026-07-26T09:12:00Z", eventType: "ProfileOpened", visibilityClass: "Access", actorDisplay: "R. Adel", summary: "Sections served", sourceService: "profile" }],
  }),
];
