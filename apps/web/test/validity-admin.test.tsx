import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ValidityPolicyAdmin } from "../src/screens/ValidityPolicyAdmin";
import { ApprovalsWorklist } from "../src/screens/ApprovalsWorklist";

function renderScreen(node: React.ReactNode, api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{node}</MemoryRouter>
    </AppProviders>,
  );
}

/**
 * The Medical Director's validity-period screen.
 *
 * <p>Two things it must say out loud, because getting either wrong is a decision made on a false belief. A
 * change is NOT retroactive — a director who shortens the window and expects yesterday's prescriptions to
 * lapse tonight would be wrong, and would find out from a patient at a counter. And "10 days because we
 * chose 10" is a different state from "10 days because nobody has looked at this"; a screen showing the
 * number either way lets the second be mistaken for the first.</p>
 */
describe("validity periods", () => {
  it("shows all four, separately", async () => {
    renderScreen(<ValidityPolicyAdmin />);

    // A course of antibiotics and a follow-up scan do not go stale at the same rate. Four settings can be
    // made equal; one cannot be split later without asking every tenant what they meant by it.
    for (const label of ["Prescriptions", "Lab orders", "Radiology orders", "Procedure orders"]) {
      expect(await screen.findByText(label)).toBeInTheDocument();
    }
  });

  it("says Radiology, not Imaging — 29.1 acceptance", async () => {
    // The stored artefact key stays `ImagingOrder`: it is a persisted config vocabulary keyed on
    // `validity.imaging-order.days`, and renaming it would rewrite live configuration to chase a label.
    // The LABEL is user-facing and was left behind by the rename — its Arabic already read الأشعة, so the
    // screen was showing a director two different names for the same setting depending on their language.
    const { container } = renderScreen(<ValidityPolicyAdmin />);
    await screen.findByText("Prescriptions");

    expect(container.textContent).not.toMatch(/imaging/i);
  });

  it("distinguishes a chosen period from the platform default", async () => {
    renderScreen(<ValidityPolicyAdmin />);
    await screen.findByText("Prescriptions");

    const chosen = screen.getByText("Prescriptions").closest("section")!;
    expect(within(chosen).getByText("Chosen")).toBeInTheDocument();

    const untouched = screen.getByText("Lab orders").closest("section")!;
    expect(within(untouched).getByText("Platform default")).toBeInTheDocument();
    expect(within(untouched).getByText(/Nobody has chosen this/i)).toBeInTheDocument();
  });

  it("says that a change is not retroactive", async () => {
    renderScreen(<ValidityPolicyAdmin />);
    // Shortening the window must not strand a patient holding a prescription they were told to come back
    // with — and a director cannot know that unless the screen says it.
    expect(await screen.findByText(/written from now on/i)).toBeInTheDocument();
  });

  it("refuses a period outside the server's own bounds", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const save = vi.spyOn(api, "setValidityPolicy");
    renderScreen(<ValidityPolicyAdmin />, api as unknown as ApiClient);
    await screen.findByText("Prescriptions");

    const card = screen.getByText("Prescriptions").closest("section")!;
    const input = within(card).getByLabelText("Days");
    await user.clear(input);
    await user.type(input, "3650");

    // The bounds come from the payload, so the screen and the endpoint cannot disagree about them. A decade
    // is not an expiry, it is a formality — and the server refuses it too.
    expect(within(card).getByRole("button", { name: "Save" })).toBeDisabled();
    expect(save).not.toHaveBeenCalled();
  });

  it("saves a period and names what it applies to", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const save = vi.spyOn(api, "setValidityPolicy");
    renderScreen(<ValidityPolicyAdmin />, api as unknown as ApiClient);
    await screen.findByText("Lab orders");

    const card = screen.getByText("Lab orders").closest("section")!;
    const input = within(card).getByLabelText("Days");
    await user.clear(input);
    await user.type(input, "21");
    await user.click(within(card).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(save).toHaveBeenCalledWith("LabOrder", 21));
  });

  it("has no serious or critical a11y violations", async () => {
    const { container } = renderScreen(<ValidityPolicyAdmin />);
    await screen.findByText("Prescriptions");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

/**
 * A validity-extension request in the approval worklist.
 *
 * <p>It has no service code, no cost and no clinical justification. Shown like every other row it reads as
 * a benefit authorization, and a reviewer opens it expecting a diagnosis that was never going to be there —
 * through an endpoint that records a PHI read on the patient's file for a question about a date.</p>
 */
describe("an extension request in the worklist", () => {
  it("says what kind of request it is, and shows the expired item", async () => {
    renderScreen(<ApprovalsWorklist />);

    expect(await screen.findByText("Validity extension")).toBeInTheDocument();
    expect(screen.getByText("RX-2026-000312")).toBeInTheDocument();
  });

  it("shows the reason without opening the clinical review", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const review = vi.spyOn(api, "approvalReview");
    renderScreen(<ApprovalsWorklist />, api as unknown as ApiClient);

    await user.click(await screen.findByText("RX-2026-000312"));

    // The reason IS the decision. Fetching the field-scoped EMR excerpt to read one logistics sentence
    // would add an audited access to the patient's record for a question that is not about the patient.
    expect(await screen.findByText(/could not travel before it lapsed/i)).toBeInTheDocument();
    expect(review).not.toHaveBeenCalled();
  });

  it("states what approving actually does", async () => {
    const user = userEvent.setup();
    renderScreen(<ApprovalsWorklist />);
    await user.click(await screen.findByText("RX-2026-000312"));

    // A reviewer choosing between "yes" and "no" needs to know that yes means a full fresh period from
    // today, not a few days added to a date that has already gone.
    expect(await screen.findByText(/resets the validity to the tenant's configured period/i)).toBeInTheDocument();
  });

  it("still opens the clinical review for an ordinary authorization", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const review = vi.spyOn(api, "approvalReview");
    renderScreen(<ApprovalsWorklist />, api as unknown as ApiClient);

    // The exception is bounded: a benefit authorization is decided on clinical context, exactly as before.
    await user.click(await screen.findByText(/MRI brain w\/ contrast/i));
    await waitFor(() => expect(review).toHaveBeenCalled());
  });
});
