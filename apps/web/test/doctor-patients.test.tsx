import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import type { ApiClient } from "../src/api/client";
import type { PatientListItem } from "@mersal/contracts";
import { DoctorPatients } from "../src/screens/ClinicianWorklists";

const IN_PROGRESS = { kind: "info", label: { en: "In progress", ar: "جارٍ" } } as const;
const COMPLETED = { kind: "ok", label: { en: "Completed", ar: "مكتمل" } } as const;

function row(over: Partial<PatientListItem> = {}): PatientListItem {
  return {
    id: "enc-1",
    beneficiaryId: "ben-9",
    name: { en: "Fatma Ibrahim", ar: "فاطمة إبراهيم" },
    mrn: "ENC-2026-000074",
    treating: true,
    lastVisit: "2026-08-01",
    status: COMPLETED,
    branchId: "br-1",
    branchName: "Maadi",
    ...over,
  };
}

/** Where we are AND how we got here — the second half is what the workspace's Back control reads. */
function Where() {
  const loc = useLocation();
  const from = (loc.state as { from?: string } | null)?.from ?? "";
  return (
    <>
      <span data-testid="where">{`${loc.pathname}${loc.search}`}</span>
      <span data-testid="from">{from}</span>
    </>
  );
}

function renderPatients(rows: PatientListItem[]) {
  const api = { listPatients: vi.fn().mockResolvedValue(rows) } as unknown as ApiClient;
  const view = render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter
        initialEntries={["/clinician/patients"]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <DoctorPatients />
        <Where />
      </MemoryRouter>
    </AppProviders>,
  );
  return { api, container: view.container };
}

/** The three visits one patient made, newest last in the array so the fold has to do the ordering. */
const AMAL = [
  row({ id: "enc-a3", beneficiaryId: "ben-1", name: { en: "Amal Hassan", ar: "أمل حسن" },
        mrn: "ENC-2026-000160", lastVisit: "2026-03-02", branchId: "br-1", branchName: "Maadi" }),
  row({ id: "enc-a1", beneficiaryId: "ben-1", name: { en: "Amal Hassan", ar: "أمل حسن" },
        mrn: "ENC-2026-000231", lastVisit: "2026-07-01", status: IN_PROGRESS,
        branchId: "br-2", branchName: "Nasr City" }),
  row({ id: "enc-a2", beneficiaryId: "ben-1", name: { en: "Amal Hassan", ar: "أمل حسن" },
        mrn: "ENC-2026-000198", lastVisit: "2026-05-14", branchId: "br-1", branchName: "Maadi" }),
];

/**
 * My Patients — one row per PERSON the treating clinician is looking after.
 *
 * <b>Why the fold is the thing under test.</b> `/encounters/mine` is a worklist of ENCOUNTERS, and it is the
 * right shape for "what have I done". This panel is asked the other question — who are my patients — and a
 * doctor who has seen the same person four times was given four rows with that person's name on them. Every
 * fixture here therefore gives at least one patient MORE THAN ONE encounter: with one visit each, a panel that
 * does not fold at all looks correct.
 */
describe("My Patients (US-030)", () => {
  it("folds a patient's several encounters into one row", async () => {
    renderPatients([...AMAL, row({ id: "enc-k", beneficiaryId: "ben-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" } })]);

    await screen.findByText("Amal Hassan");
    // Three encounters, one row. `getAllByText` rather than `getByText` would pass on the unfolded version.
    expect(screen.getAllByText("Amal Hassan")).toHaveLength(1);
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("takes the branch and the last visit from the most recent encounter", async () => {
    // Amal's newest visit (1 Jul) was at Nasr City; her two older ones were at Maadi. A fold that took the
    // first row it saw would say Maadi, which is the branch of a visit four months ago sitting beside a date
    // that is not that visit's.
    renderPatients(AMAL);

    const row0 = (await screen.findByText("Amal Hassan")).closest("tr")!;
    expect(within(row0).getByText("Nasr City")).toBeInTheDocument();
    expect(within(row0).getByText(/1 Jul 2026|01 Jul 2026/)).toBeInTheDocument();
  });

  it("says so when a walk-in has no branch, rather than leaving the cell blank", async () => {
    // An encounter with no appointment has no branch; emr sends null and the panel states it. A blank cell
    // reads as a rendering fault rather than as a fact about the visit.
    renderPatients([row({ branchId: null, branchName: null })]);

    const row0 = (await screen.findByText("Fatma Ibrahim")).closest("tr")!;
    expect(within(row0).getByText("—")).toBeInTheDocument();
  });

  it("filters by branch", async () => {
    const user = userEvent.setup();
    renderPatients([
      ...AMAL,   // latest visit: Nasr City
      row({ id: "enc-k", beneficiaryId: "ben-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" },
            branchId: "br-1", branchName: "Maadi" }),
    ]);

    await screen.findByText("Amal Hassan");
    await user.click(screen.getByRole("button", { name: /maadi/i }));

    await waitFor(() => expect(screen.queryByText("Amal Hassan")).not.toBeInTheDocument());
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("offers no branch filter to a doctor who works one branch", async () => {
    // A group whose single option matches every row filters nothing — it is a control that costs a click to
    // discover and cannot change what is on screen.
    renderPatients([row(), row({ id: "enc-2", beneficiaryId: "ben-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" } })]);

    await screen.findByText("Fatma Ibrahim");
    expect(screen.queryByRole("button", { name: /^maadi/i })).not.toBeInTheDocument();
  });

  it("searches across the name, the branch and EVERY encounter reference", async () => {
    const user = userEvent.setup();
    renderPatients([...AMAL, row({ id: "enc-k", beneficiaryId: "ben-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" } })]);
    await screen.findByText("Amal Hassan");

    // ENC-2026-000198 is Amal's MIDDLE visit, not her most recent. A haystack built from the folded row alone
    // would know only the latest reference, and a doctor holding the slip for an older visit would be told
    // there are no matches.
    await user.type(screen.getByRole("searchbox"), "ENC-2026-000198");

    await waitFor(() => expect(screen.queryByText("Khaled Mostafa")).not.toBeInTheDocument());
    expect(screen.getByText("Amal Hassan")).toBeInTheDocument();
  });

  it("pages on unique patients, not on encounters", async () => {
    renderPatients(
      Array.from({ length: 13 }, (_, i) =>
        row({
          id: `enc-${i}`,
          beneficiaryId: `ben-${i}`,
          name: { en: `Patient ${String(i).padStart(2, "0")}`, ar: `مريض ${i}` },
          // Descending by last visit, so Patient 00 (the most recent) leads and 10–12 fall to page 2.
          lastVisit: `2026-08-${String(20 - i).padStart(2, "0")}`,
        })),
    );

    expect(await screen.findByText("Patient 00")).toBeInTheDocument();
    expect(screen.getByText("Patient 09")).toBeInTheDocument();
    expect(screen.queryByText("Patient 10")).not.toBeInTheDocument();
  });

  it("lists every previous encounter, newest first, with its date and status", async () => {
    const user = userEvent.setup();
    renderPatients(AMAL);

    await screen.findByText("Amal Hassan");
    await user.click(screen.getByRole("button", { name: /encounters \(3\)/i }));

    const dialog = await screen.findByRole("dialog");
    const visits = within(dialog).getAllByRole("button", { name: /open this encounter/i });
    expect(visits).toHaveLength(3);
    // Newest first — the array handed to the panel was deliberately out of order.
    expect(visits[0]).toHaveAccessibleName(/1 Jul 2026|01 Jul 2026/);
    expect(visits[0]).toHaveAccessibleName(/in progress/i);
    expect(visits[2]).toHaveAccessibleName(/2 Mar 2026|02 Mar 2026/);
  });

  it("opens the encounter a visit names, and records where to come back to", async () => {
    const user = userEvent.setup();
    renderPatients(AMAL);

    await screen.findByText("Amal Hassan");
    await user.click(screen.getByRole("button", { name: /encounters \(3\)/i }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getAllByRole("button", { name: /open this encounter/i })[1]);

    // The MIDDLE visit — so a handler that always opened the newest, or the first in the unsorted array,
    // fails here rather than passing on a coincidence.
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/clinician/encounter?encounter=enc-a2"));
    // Without this the workspace falls back to navigate(-1), which is wrong after a redirect and renders no
    // Back control at all on a pasted deep link.
    expect(screen.getByTestId("from")).toHaveTextContent("/clinician/patients");
  });

  it("opens the patient file from a row", async () => {
    const user = userEvent.setup();
    renderPatients([row()]);
    await user.click(await screen.findByRole("button", { name: /patient file/i }));
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/patients/ben-9"));
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderPatients(AMAL);
    await screen.findByText("Amal Hassan");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
