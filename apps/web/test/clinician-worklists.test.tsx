import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import type { ApiClient } from "../src/api/client";
import type { OrderRow, PatientListItem, RxRow } from "@mersal/contracts";
import { DoctorOrders, DoctorPrescriptions } from "../src/screens/ClinicianWorklists";

const BEN_A = "aaaaaaaa-0000-0000-0000-000000000231";
const BEN_UNKNOWN = "aaaaaaaa-0000-0000-0000-0000000009ff";
const ACTIVE = { kind: "info", label: { en: "Active", ar: "نشط" } } as const;

/** The doctor's encounters — the ONLY place a name for these ids may come from. See `usePatientNames`. */
const PATIENTS: PatientListItem[] = [{
  id: "enc-1", beneficiaryId: BEN_A, name: { en: "Amal Hassan", ar: "أمل حسن" },
  mrn: "ENC-2026-000231", treating: true, lastVisit: "2026-07-01",
  status: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
  branchId: "br-1", branchName: "Maadi",
}];

function order(over: Partial<OrderRow> = {}): OrderRow {
  return {
    id: "ord-1", orderNo: "ORD-2026-000118",
    beneficiary: { id: BEN_A, token: "•••4821" },
    orderType: "Lab", primaryCode: "80053", lineCount: 2, status: ACTIVE,
    requestedAt: "2026-07-22T08:10:00Z", firstLineId: "ln-1", expiresAt: "2026-08-21T08:10:00Z",
    encounterId: "enc-1",
    lines: [
      { id: "ln-1", code: "80053", codeSystem: "CPT", description: "Comprehensive metabolic panel",
        quantityOrdered: 1, quantityConsumed: 0, status: ACTIVE },
      // Undescribed on purpose — the dialog must state the gap, not paper over it.
      { id: "ln-2", code: "84443", codeSystem: "CPT", description: null,
        quantityOrdered: 2, quantityConsumed: 1, status: ACTIVE },
    ],
    ...over,
  };
}

function rx(over: Partial<RxRow> = {}): RxRow {
  return {
    id: "rx-1", rxNo: "RX-2026-000202", beneficiary: { id: BEN_A, token: "•••4821" },
    lineCount: 1, status: ACTIVE, submittedAt: "2026-07-22T08:15:00Z",
    expiresAt: "2026-08-21T08:15:00Z", prescriber: { en: "Dr Karim", ar: "د. كريم" },
    encounterId: "enc-1",
    lines: [{
      id: "rx-1-l1", drug: { en: "Amoxicillin 500mg capsule", ar: "أموكسيسيلين ٥٠٠" },
      dose: "500 mg", route: "PO", frequency: "TDS",
      quantityPrescribed: 21, quantityDispensed: 0, refillsAllowed: 0, status: ACTIVE,
    }],
    ...over,
  };
}

function renderScreen(ui: React.ReactElement, api: Partial<ApiClient>) {
  return render(
    <AppProviders
      authClient={new DevAuthClient()}
      apiClient={{ listPatients: vi.fn().mockResolvedValue(PATIENTS), ...api } as unknown as ApiClient}
    >
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{ui}</MemoryRouter>
    </AppProviders>,
  );
}

const renderOrders = (rows: OrderRow[]) =>
  renderScreen(<DoctorOrders />, { ordersMine: vi.fn().mockResolvedValue(rows) });
const renderRx = (rows: RxRow[]) =>
  renderScreen(<DoctorPrescriptions />, { prescriptionsMine: vi.fn().mockResolvedValue(rows) });

/**
 * Orders and Prescriptions — the ordering clinician's own work, read back.
 *
 * <b>On the name.</b> Neither orders-service nor pharmacy-service holds a beneficiary name, and neither may
 * fetch one — a service reading a sibling's data on the caller's behalf is the aggregation shape this
 * platform forbids. patient-service's name-only `/beneficiaries/summaries` needs `patient:read`, which the
 * doctor role does not hold. So the client joins these rows against `/encounters/mine`, where emr already
 * gives the TREATING clinician the names, and falls back to the masked token when it cannot. The fallback is
 * the point of the second test: the failure mode has to be "less informative", never "wrong patient".
 */
describe("Orders worklist", () => {
  it("shows the patient's name rather than the masked token", async () => {
    renderOrders([order()]);
    expect(await screen.findByText("Amal Hassan")).toBeInTheDocument();
    expect(screen.queryByText("•••4821")).not.toBeInTheDocument();
  });

  it("keeps the masked token when the name cannot be resolved", async () => {
    // `/encounters/mine` returns the 100 most recent, so an order for a patient who has dropped off the end
    // of that list resolves to nothing. It must degrade to what the board always showed.
    renderOrders([order({ beneficiary: { id: BEN_UNKNOWN, token: "•••9999" } })]);
    expect(await screen.findByText("•••9999")).toBeInTheDocument();
  });

  it("counts the lines on the row, as the prescriptions board does", async () => {
    renderOrders([order()]);
    const row = (await screen.findByText("Amal Hassan")).closest("tr")!;
    expect(within(row).getByText("2")).toBeInTheDocument();
  });

  it("opens the order, with every line, when the row is clicked", async () => {
    const user = userEvent.setup();
    renderOrders([order()]);
    await user.click(await screen.findByText("Amal Hassan"));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/ORD-2026-000118/)).toBeInTheDocument();
    // BOTH lines — the worklist used to carry only `lines[0].code`, so a two-line order showed one test.
    expect(within(dialog).getByText("Comprehensive metabolic panel")).toBeInTheDocument();
    // The undescribed line states the gap rather than rendering blank, and still shows its code.
    expect(within(dialog).getByText(/test name not recorded/i)).toBeInTheDocument();
    expect(within(dialog).getByText("CPT 84443")).toBeInTheDocument();
    // Ordered and performed stay apart — never folded into one "remaining". One pair per line, so both
    // labels appear twice; a single-match assertion here would fail for the wrong reason.
    expect(within(dialog).getAllByText(/quantity ordered/i)).toHaveLength(2);
    expect(within(dialog).getAllByText(/performed to date/i)).toHaveLength(2);
  });

  it("finds an order by a line that is not the primary code", async () => {
    const user = userEvent.setup();
    renderOrders([order(), order({ id: "ord-2", orderNo: "ORD-2026-000119", lines: [], lineCount: 0 })]);
    await screen.findByText("ORD-2026-000118");

    // 84443 is the SECOND line. The "Code" column shows only the first, so a haystack built from the row's
    // visible cells would miss it.
    await user.type(screen.getByRole("searchbox"), "84443");
    await waitFor(() => expect(screen.queryByText("ORD-2026-000119")).not.toBeInTheDocument());
    expect(screen.getByText("ORD-2026-000118")).toBeInTheDocument();
  });

  it("opens the TIMELINE from the row without also opening the detail dialog", async () => {
    const user = userEvent.setup();
    const encounterTimeline = vi.fn().mockResolvedValue([
      { status: "OrderPlaced", at: "2026-07-22T08:10:00Z", by: null, byName: null,
        source: "orders", reference: "ORD-2026-000118" },
    ]);
    renderScreen(<DoctorOrders />, {
      ordersMine: vi.fn().mockResolvedValue([order()]),
      encounterTimeline,
    });

    await screen.findByText("Amal Hassan");
    await user.click(screen.getByRole("button", { name: /timeline/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/investigation ordered/i)).toBeInTheDocument();
    expect(encounterTimeline).toHaveBeenCalledWith("enc-1");

    // The trap this column has to avoid: the row is itself clickable, so without stopPropagation the press
    // fires the row's handler too and the order dialog opens UNDERNEATH the timeline.
    //
    // Counted off the raw DOM rather than by role. Radix marks a covered dialog aria-hidden, so a second one
    // stacked below is invisible to getAllByRole("dialog") — the first version of this check asserted on the
    // top dialog's contents and passed happily with the defect reintroduced.
    expect(document.querySelectorAll('[role="dialog"]')).toHaveLength(1);
  });

  it("closes the timeline without opening the row behind it", async () => {
    const user = userEvent.setup();
    renderScreen(<DoctorOrders />, {
      ordersMine: vi.fn().mockResolvedValue([order()]),
      encounterTimeline: vi.fn().mockResolvedValue([
        { status: "OrderPlaced", at: "2026-07-22T08:10:00Z", by: null, byName: null,
          source: "orders", reference: "ORD-2026-000118" },
      ]),
    });

    await screen.findByText("Amal Hassan");

    // `Modal` renders through a React PORTAL, so the dialog is a child of document.body in the DOM — but
    // React dispatches synthetic events along the REACT tree, where it is still a descendant of this table
    // cell. Every click inside the open dialog therefore reached the row's onClick, and DISMISSING the
    // timeline opened the row's own detail dialog behind it. Escape was clean, because a key is not a click.
    for (const dismiss of ["footer", "escape"] as const) {
      await user.click(screen.getByRole("button", { name: /timeline/i }));
      const dialog = await screen.findByRole("dialog");
      expect(within(dialog).getByText(/investigation ordered/i)).toBeInTheDocument();

      if (dismiss === "escape") await user.keyboard("{Escape}");
      else await user.click(within(dialog).getAllByRole("button", { name: /close/i })[0]);

      // Counted off the raw DOM: Radix marks a covered dialog aria-hidden, so a detail dialog opening
      // underneath is invisible to getAllByRole("dialog").
      await waitFor(() => expect(document.querySelectorAll('[role="dialog"]')).toHaveLength(0));
    }
  });

  it("filters by date window", async () => {
    const user = userEvent.setup();
    const now = Date.now();
    const daysAgo = (n: number) => new Date(now - n * 24 * 60 * 60 * 1000).toISOString();
    renderOrders([
      order({ id: "recent", orderNo: "ORD-RECENT", requestedAt: daysAgo(5) }),
      order({ id: "old", orderNo: "ORD-OLD", requestedAt: daysAgo(200) }),
    ]);

    await screen.findByText("ORD-RECENT");
    expect(screen.getByText("ORD-OLD")).toBeInTheDocument();

    // Relative to NOW rather than to a fixture date: the cutoff is computed at match time, so a window
    // pinned to a hardcoded calendar date would start failing on its own the day the fixture aged out.
    await user.click(screen.getByRole("button", { name: /last 30 days/i }));
    await waitFor(() => expect(screen.queryByText("ORD-OLD")).not.toBeInTheDocument());
    expect(screen.getByText("ORD-RECENT")).toBeInTheDocument();

    // Pressing the active chip clears it — the first thing anyone tries.
    await user.click(screen.getByRole("button", { name: /last 30 days/i }));
    await waitFor(() => expect(screen.getByText("ORD-OLD")).toBeInTheDocument());
  });

  it("reveals the date fields only when Custom is pressed, and narrows nothing until one is filled", async () => {
    const user = userEvent.setup();
    const now = Date.now();
    const daysAgo = (n: number) => new Date(now - n * 24 * 60 * 60 * 1000).toISOString();
    renderOrders([
      order({ id: "recent", orderNo: "ORD-RECENT", requestedAt: daysAgo(5) }),
      order({ id: "old", orderNo: "ORD-OLD", requestedAt: daysAgo(200) }),
    ]);
    await screen.findByText("ORD-RECENT");

    // The fields belong to the chip that reveals them; two permanently-visible date inputs would make that
    // chip look inert.
    expect(screen.queryByLabelText(/^from$/i)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /custom date/i }));
    const from = await screen.findByLabelText(/^from$/i);
    expect(screen.getByLabelText(/^to$/i)).toBeInTheDocument();

    // An unnamed period narrows nothing. Emptying the table the instant the chip is pressed would read as
    // "there is nothing in this period" about a period nobody has named yet.
    expect(screen.getByText("ORD-OLD")).toBeInTheDocument();
    expect(screen.getByText("ORD-RECENT")).toBeInTheDocument();

    // One bound alone is a real answer: a From with no To means "since then".
    const cutoff = new Date(now - 30 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10);
    await user.type(from, cutoff);
    await waitFor(() => expect(screen.queryByText("ORD-OLD")).not.toBeInTheDocument());
    expect(screen.getByText("ORD-RECENT")).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderOrders([order()]);
    await screen.findByText("Amal Hassan");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

describe("Prescriptions worklist", () => {
  it("shows the patient's name rather than the masked token", async () => {
    renderRx([rx()]);
    expect(await screen.findByText("Amal Hassan")).toBeInTheDocument();
    expect(screen.queryByText("•••4821")).not.toBeInTheDocument();
  });

  it("opens the prescription, with its lines, when the row is clicked", async () => {
    const user = userEvent.setup();
    renderRx([rx()]);
    await user.click(await screen.findByText("Amal Hassan"));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/RX-2026-000202/)).toBeInTheDocument();
    expect(within(dialog).getByText("Amoxicillin 500mg capsule")).toBeInTheDocument();
    expect(within(dialog).getByText("TDS")).toBeInTheDocument();
  });
});

/**
 * "Approved" is a claim that a person approved it.
 *
 * `RxStatus.Approved` is reached two ways (doc 23 §3 — "approve / no-gate", actor "Approval Team / auto"):
 * the approval team decides it, or `RxRoutingPolicy` finds no gate and the SUBMIT path sets it outright
 * (`if (!route.RequiresApproval) rx.Status = RxStatus.Approved`). Both rendered the same chip, so a
 * prescriber was told a reviewer had passed a prescription no reviewer had ever seen.
 *
 * These run against `rxStatus` through the real HTTP client, because the mapping is where the defect lived —
 * a screen-level test would pass whatever the mapper handed it.
 */
describe("Prescription status vocabulary", () => {
  const mapped = async (status: string, authorizationId?: string) => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true, status: 200,
      headers: new Headers({ "content-type": "application/json" }),
      json: async () => [{
        prescriptionId: "11111111-1111-1111-1111-111111111111",
        rxNo: "RX-1", beneficiaryId: "22222222-2222-2222-2222-222222222222",
        encounterId: "33333333-3333-3333-3333-333333333333",
        status, authorizationId, lines: [],
      }],
    });
    vi.stubGlobal("fetch", fetchMock);
    try {
      const rows = await new HttpApiClient().prescriptionsMine();
      return rows[0].status;
    } finally {
      vi.unstubAllGlobals();
    }
  };

  it("calls an auto-cleared prescription Verified, not Approved", async () => {
    const s = await mapped("Approved");
    expect(s.label.en).toBe("Verified");
    expect(s.label.en).not.toBe("Approved");
  });

  it("calls it Approved only when an authorization records the decision", async () => {
    const s = await mapped("Approved", "44444444-4444-4444-4444-444444444444");
    expect(s.label.en).toBe("Approved");
  });

  it("says a gated prescription is awaiting approval rather than leaving it unnamed", async () => {
    // `Submitted` is where a gated prescription waits. It used to fall through to the default and render
    // "Approved" — the exact opposite of its meaning.
    const s = await mapped("Submitted");
    expect(s.label.en).toBe("Awaiting approval");
    expect(s.kind).toBe("warn");
  });

  it("keeps Dispensed for the pharmacy's own step", async () => {
    expect((await mapped("Dispensed")).label.en).toBe("Dispensed");
    expect((await mapped("PartiallyDispensed")).label.en).toBe("Partially dispensed");
  });
});
