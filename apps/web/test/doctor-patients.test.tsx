import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
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
    ...over,
  };
}

function Where() {
  return <span data-testid="where">{useLocation().pathname}</span>;
}

function renderPatients(rows: PatientListItem[]) {
  const api = { listPatients: vi.fn().mockResolvedValue(rows) } as unknown as ApiClient;
  const view = render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <DoctorPatients />
        <Where />
      </MemoryRouter>
    </AppProviders>,
  );
  return { api, container: view.container };
}

/**
 * My Patients — the treating clinician's own encounters.
 *
 * The worklist used to render "Beneficiary •••4821" on every row, because `/encounters/mine` carried no name
 * and the client had only the id to mask. A clinician cannot pick their patient out of a list of identical
 * masks, and they are entitled to the name: they read the full record behind each row.
 */
describe("My Patients (US-030)", () => {
  it("shows the patient's name", async () => {
    renderPatients([row(), row({ id: "enc-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" } })]);
    expect(await screen.findByText("Fatma Ibrahim")).toBeInTheDocument();
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("falls back to the masked token for a walk-in that was never booked", async () => {
    // emr sends no name for an encounter with no appointment, so the client masks the id — honest, where a
    // blank cell would read as data loss.
    renderPatients([row({ name: { en: "Beneficiary •••4821", ar: "Beneficiary •••4821" } })]);
    expect(await screen.findByText("Beneficiary •••4821")).toBeInTheDocument();
  });

  it("searches across name, MRN and status", async () => {
    const user = userEvent.setup();
    renderPatients([row(), row({ id: "enc-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" } })]);

    await screen.findByText("Fatma Ibrahim");
    await user.type(screen.getByRole("searchbox"), "khaled");

    await waitFor(() => expect(screen.queryByText("Fatma Ibrahim")).not.toBeInTheDocument());
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("filters by encounter status", async () => {
    const user = userEvent.setup();
    renderPatients([
      row(),
      row({ id: "enc-2", name: { en: "Khaled Mostafa", ar: "خالد مصطفى" }, status: IN_PROGRESS }),
    ]);

    await screen.findByText("Fatma Ibrahim");
    await user.click(screen.getByRole("button", { name: /in progress/i }));

    await waitFor(() => expect(screen.queryByText("Fatma Ibrahim")).not.toBeInTheDocument());
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("pages a panel that does not fit on one screen", async () => {
    renderPatients(
      Array.from({ length: 13 }, (_, i) =>
        row({
          id: `enc-${i}`,
          name: { en: `Patient ${String(i).padStart(2, "0")}`, ar: `مريض ${i}` },
          // Descending by last visit, so Patient 00 (the most recent) leads and 10–12 fall to page 2.
          lastVisit: `2026-08-${String(20 - i).padStart(2, "0")}`,
        })),
    );

    expect(await screen.findByText("Patient 00")).toBeInTheDocument();
    expect(screen.getByText("Patient 09")).toBeInTheDocument();
    expect(screen.queryByText("Patient 10")).not.toBeInTheDocument();
  });

  it("opens the patient file from a row", async () => {
    const user = userEvent.setup();
    renderPatients([row()]);
    await user.click(await screen.findByRole("button", { name: /patient file/i }));
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/patients/ben-9"));
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderPatients([row()]);
    await screen.findByText("Fatma Ibrahim");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
