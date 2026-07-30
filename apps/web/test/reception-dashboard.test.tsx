import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ReceptionDashboard } from "../src/screens/ReceptionDashboard";

/**
 * 14.5 — the reception dashboard.
 *
 * Three claims worth pinning: the cards come from the SERVER (not from tallying a capped board), today's
 * visits show real names for people who have arrived, and doctor + specialty are joined in from
 * provider-service rather than expected from emr.
 */
describe("Reception dashboard", () => {
  const visitsTable = () => within(screen.getByRole("table", { name: /today's visits/i }));

  it("takes the card figures from the server, not from counting the board", async () => {
    const counts = vi.fn().mockResolvedValue({ total: 137, checkedIn: 12, noShow: 4 });
    const api = new DevApiClient({ latencyMs: 0 });
    (api as unknown as { appointmentCounts: unknown }).appointmentCounts = counts;
    renderNode(<ReceptionDashboard />, api as unknown as ApiClient);

    // 137 could never come from the board: that read is capped at 200 rows and the fixture holds four.
    expect(await screen.findByText("137")).toBeInTheDocument();
    expect(screen.getByText("12")).toBeInTheDocument();
    expect(screen.getByText("4")).toBeInTheDocument();
    expect(counts).toHaveBeenCalled();
  });

  it("lists only patients who have ARRIVED, by name", async () => {
    renderNode(<ReceptionDashboard />);

    // The fixture's checked-in row carries a name (captured at check-in); the booked ones do not, and are
    // not in this section at all — "today's visits" is the people in the building.
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());
    expect(visitsTable().getAllByRole("row")).toHaveLength(2);   // header + the one arrival
  });

  it("joins the doctor's NAME and SPECIALTY in from provider-service", async () => {
    renderNode(<ReceptionDashboard />);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());

    // emr returns only a doctorId. Who that is belongs to provider-service, which reception reads directly
    // under practitioner:read — neither service composes the other's data on the caller's behalf.
    const row = visitsTable().getByText("Amal Hassan").closest("tr")!;
    expect(within(row).getByText("Hana Mansour")).toBeInTheDocument();
    expect(within(row).getByText("Pediatrics")).toBeInTheDocument();
  });

  it("gives every visit a prominent patient-file action", async () => {
    renderNode(<ReceptionDashboard />);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());

    expect(visitsTable().getByRole("button", { name: /patient file/i })).toBeInTheDocument();
  });

  it("lays the whole day out in hour bands, including hours with nothing in them", async () => {
    renderNode(<ReceptionDashboard />);

    // An empty band is the answer to "can we fit a walk-in in?", so the schedule is built from a fixed hour
    // list rather than only from the hours that happen to be booked.
    expect(await screen.findByText("08:00")).toBeInTheDocument();
    expect(screen.getByText("15:00")).toBeInTheDocument();
  });

  it("opens a booking note from the visits row", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    // Put a note on the arrival, so the affordance has something to open.
    const original = api.appointments.bind(api);
    (api as unknown as { appointments: unknown }).appointments = async (...args: unknown[]) => {
      const rows = await (original as (...a: unknown[]) => Promise<any[]>)(...args);
      return rows.map((r) => (r.checkedIn ? { ...r, note: "Wheelchair access needed." } : r));
    };
    renderNode(<ReceptionDashboard />, api as unknown as ApiClient);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());

    await user.click(visitsTable().getByRole("button", { name: /appointment note/i }));
    expect(within(await screen.findByRole("dialog")).getByText(/wheelchair access/i)).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderNode(<ReceptionDashboard />);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
