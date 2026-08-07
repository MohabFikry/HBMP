import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderApp } from "./helpers";

/**
 * 29.2b / design 45 §2b — the external delivering provider's portal.
 *
 * <p>What is asserted here is what the SCREEN must get right; the ownership rule itself is server-side and
 * proved by the two-provider test in orders. The UI's own obligations are narrower and easy to get wrong:
 * progress must read identically to the doctor's worklist, a withheld referral reason must not render as
 * "none", and a double-tap must not appear to burn two visits.</p>
 */
describe("29.2b — procedure delivery centre", () => {
  it("shows the queue with progress in the same words the doctor's worklist uses", async () => {
    renderApp("/procedure/queue", "procedure_provider");

    // "4 of 6 sessions delivered" — a course that reads differently at each end is one somebody delivers twice.
    expect(await screen.findByText(/0 of 6 sessions delivered/i)).toBeInTheDocument();
    expect(await screen.findByText(/0 of 12 sessions delivered/i)).toBeInTheDocument();
  });

  it("renders a withheld referral reason as 'not disclosed', never as 'none'", async () => {
    renderApp("/procedure/queue", "procedure_provider");

    // The dialysis fixture carries sharedClinicalContext = null: the ordering doctor chose to share nothing.
    // A physiotherapist who reads that as "no relevant history" treats someone as uncomplicated who is not.
    expect(await screen.findByText(/not disclosed/i)).toBeInTheDocument();
    expect(screen.queryByText(/^none$/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/no diagnosis/i)).not.toBeInTheDocument();
  });

  it("shows the clinical context the doctor DID choose to share", async () => {
    renderApp("/procedure/queue", "procedure_provider");

    expect(await screen.findByText(/Post-op knee rehabilitation/i)).toBeInTheDocument();
  });

  it("never shows a diagnosis, coverage amount or claim value", async () => {
    const { container } = renderApp("/procedure/queue", "procedure_provider");
    await screen.findByText(/0 of 6 sessions delivered/i);

    const text = container.textContent?.toLowerCase() ?? "";
    for (const forbidden of ["diagnosis", "icd-", "coverage", "cost-share", "copay", "claim", "egp"]) {
      expect(text).not.toContain(forbidden);
    }
  });

  it("advances progress by exactly one when a session is recorded", async () => {
    const user = userEvent.setup();
    renderApp("/procedure/queue", "procedure_provider");
    await screen.findByText(/0 of 6 sessions delivered/i);

    const rows = screen.getAllByRole("row");
    const physio = rows.find((r) => within(r).queryByText(/97110/));
    await user.click(within(physio!).getByRole("button", { name: /record session/i }));

    await waitFor(() => expect(screen.getByText(/1 of 6 sessions delivered/i)).toBeInTheDocument());
    // The OTHER order is untouched — sessions are per line, not a shared counter.
    expect(screen.getByText(/0 of 12 sessions delivered/i)).toBeInTheDocument();
  });

  it("refuses a counter lookup with only one identifier", async () => {
    const user = userEvent.setup();
    renderApp("/procedure/counter", "procedure_provider");

    await user.type(await screen.findByLabelText(/card number/i), "CARD-123");
    await user.click(screen.getByRole("button", { name: /verify/i }));

    // A card number is a lookup key, not an authenticator — cards are shared and photographed.
    expect(await screen.findByText(/card number on its own is not enough/i)).toBeInTheDocument();
  });

  it("resolves the person once two identifiers are given", async () => {
    const user = userEvent.setup();
    renderApp("/procedure/counter", "procedure_provider");

    await user.type(await screen.findByLabelText(/card number/i), "CARD-123");
    await user.type(screen.getByLabelText(/member number/i), "MEM-9");
    await user.click(screen.getByRole("button", { name: /verify/i }));

    expect(await screen.findByText(/97110/)).toBeInTheDocument();
    expect(screen.queryByText(/card number on its own is not enough/i)).not.toBeInTheDocument();
  });

  it("prompts before a lookup rather than showing an empty result", async () => {
    renderApp("/procedure/counter", "procedure_provider");

    // "Absence of data is never a clean result": before anyone searches, the screen must not imply the
    // person has nothing.
    expect(await screen.findByText(/enter two identifiers to begin/i)).toBeInTheDocument();
    expect(screen.queryByText(/no sessions are routed/i)).not.toBeInTheDocument();
  });
});
