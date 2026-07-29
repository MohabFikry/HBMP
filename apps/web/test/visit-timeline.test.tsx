import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { AppointmentRow, TimelineStep } from "@mersal/contracts";
import { VisitTimelineButton } from "../src/screens/VisitTimeline";

const row: AppointmentRow = {
  id: "appt-1",
  beneficiary: { id: "ben-1", token: "•••4821" },
  appointmentType: "Consultation",
  status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } },
  scheduledStart: "2026-07-22T09:00:00Z",
  checkInEligible: false,
  checkedIn: true,
  noShowEligible: false,
  startVisitEligible: true,
};

const steps: TimelineStep[] = [
  // Real actors are subject GUIDs, not names — the fixtures reflect that.
  // A resolved name, an unattributed step, and an actor whose name could NOT be resolved — the three states.
  { status: "Booked", at: "2026-07-22T08:00:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Nada Reception" },
  { status: "CheckedIn", at: "2026-07-22T08:55:00Z", by: null, byName: null },
  { status: "NoShow", at: "2026-07-22T09:40:00Z", by: "129d2a05-8c27-43c7-aae2-f2cc4c7fda30", byName: null },
];

function renderBtn(api: ApiClient) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <VisitTimelineButton row={row} />
      </MemoryRouter>
    </AppProviders>,
  );
}

const fakeApi = (over: Partial<ApiClient> = {}): ApiClient =>
  ({ appointmentTimeline: vi.fn().mockResolvedValue(steps), ...over }) as unknown as ApiClient;

/** 23 §1 — "all should be tracked, and there is a button on the visit to see the timeline". */
describe("Visit timeline", () => {
  it("does not fetch until opened — a day board is dozens of rows", async () => {
    const appointmentTimeline = vi.fn().mockResolvedValue(steps);
    const user = userEvent.setup();
    renderBtn(fakeApi({ appointmentTimeline }));

    expect(appointmentTimeline).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: /timeline/i }));
    await waitFor(() => expect(appointmentTimeline).toHaveBeenCalledWith("appt-1"));
  });

  it("shows each step in order, as an ordered list", async () => {
    const user = userEvent.setup();
    renderBtn(fakeApi());
    await user.click(screen.getByRole("button", { name: /timeline/i }));

    const list = await screen.findByRole("list");
    const items = within(list).getAllByRole("listitem");
    expect(items).toHaveLength(3);
    // The sequence IS the content, so it must be a real ordered list, not a stack of divs.
    expect(list.tagName).toBe("OL");
    expect(items[0]).toHaveTextContent(/booked/i);
    expect(items[1]).toHaveTextContent(/checked in/i);
    expect(items[2]).toHaveTextContent(/no-show/i);
  });

  it("names who performed each step, and says so when nobody was recorded", async () => {
    const user = userEvent.setup();
    renderBtn(fakeApi());
    await user.click(screen.getByRole("button", { name: /timeline/i }));

    const items = within(await screen.findByRole("list")).getAllByRole("listitem");
    // Resolved: a NAME, not a GUID, and no identifier styling.
    expect(items[0]).toHaveTextContent("Nada Reception");
    expect(items[0]).not.toHaveTextContent("0cccc773");
    // Falling back to the booker would claim they checked the patient in — a lie the desk would act on.
    expect(items[1]).toHaveTextContent(/actor not recorded/i);
    expect(items[1]).not.toHaveTextContent("Nada Reception");
    // Unresolvable: the id still shows, truncated and monospaced, full value in the title. An approximate actor
    // would be worse than a visible identifier.
    expect(items[2]).toHaveTextContent("129d2a05");
    expect(within(items[2]).getByTitle(/129d2a05-8c27-43c7-aae2-f2cc4c7fda30/)).toBeInTheDocument();
  });

  it("an empty history says so rather than showing an empty box", async () => {
    const user = userEvent.setup();
    renderBtn(fakeApi({ appointmentTimeline: vi.fn().mockResolvedValue([]) }));
    await user.click(screen.getByRole("button", { name: /timeline/i }));
    expect(await screen.findByText(/no recorded history/i)).toBeInTheDocument();
  });

  it("a refused read is reported as a refusal, not as 'no history'", async () => {
    const user = userEvent.setup();
    renderBtn(fakeApi({
      appointmentTimeline: vi.fn().mockRejectedValue(new ApiError("http", "branch-scope-denied", 403)),
    }));
    await user.click(screen.getByRole("button", { name: /timeline/i }));

    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(/no recorded history/i)).not.toBeInTheDocument();
  });

  it("renders an unknown status plainly instead of giving it a reassuring colour", async () => {
    const user = userEvent.setup();
    renderBtn(fakeApi({
      appointmentTimeline: vi.fn().mockResolvedValue([{ status: "SomeNewState", at: "2026-07-22T08:00:00Z", by: "x" }]),
    }));
    await user.click(screen.getByRole("button", { name: /timeline/i }));
    expect(await screen.findByText("SomeNewState")).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const user = userEvent.setup();
    const { baseElement } = renderBtn(fakeApi());
    await user.click(screen.getByRole("button", { name: /timeline/i }));
    await screen.findByRole("list");
    // baseElement, not container: the modal renders in a portal outside the container.
    expect(await axe(baseElement, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
