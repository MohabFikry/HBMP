import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { DocumentValidityAdmin } from "../src/screens/DocumentValidityAdmin";

/**
 * How long a document is good for, and how early its lapse is warned about (ADR-0035 §6).
 *
 * <p><b>What this replaces.</b> `PractitionerLicence.WarningDays = [90, 60, 30]` — a compiled-in constant,
 * which meant the one number a Medical Director most obviously owns was the one they could not touch.</p>
 *
 * <p>Two things these tests guard beyond the form working. First, the renewal period is NOT an override: the
 * platform does not decide when a government-issued card lapses, and a screen that implied it did would be
 * inventing a fact about a refugee's papers. Second, the thresholds can never be emptied — "warn at no point"
 * would silence an expiring credential completely, and the failure would look exactly like a quiet week.</p>
 */

function render(api: ApiClient = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient) {
  return renderNode(<DocumentValidityAdmin />, api);
}

const rowFor = (kind: string) => screen.getByText(kind).closest(".dv-row") as HTMLElement;

describe("document validity", () => {
  it("says plainly that the renewal period does not override a printed expiry", async () => {
    render();
    // The most important sentence on the page. Mersal does not decide when somebody's papers expire; where an
    // expiry was recorded, that is the one that counts.
    expect(await screen.findByText(/does NOT override an expiry printed on a document/i)).toBeInTheDocument();
  });

  it("separates the documents that stop somebody being SEEN from those that stop somebody PRACTISING", async () => {
    render();
    // Two different consequences reached by two different paths. One table under one heading would put the
    // judgement about a refugee card beside the judgement about a licence as if they were the same call.
    expect(await screen.findByText("Beneficiary identity")).toBeInTheDocument();
    expect(screen.getByText("Provider credentials")).toBeInTheDocument();
    expect(screen.getByText(/stops somebody being SEEN/i)).toBeInTheDocument();
    expect(screen.getByText(/stops somebody PRACTISING/i)).toBeInTheDocument();
  });

  it("distinguishes a chosen value from an unlooked-at default", async () => {
    render();
    await screen.findByText("RefugeeId");

    // "730 because we chose 730" and "365 because nobody has looked" are different states, and only one of
    // them is a decision. A screen showing both as plain numbers reports a policy nobody set as a policy.
    expect(within(rowFor("RefugeeId")).getByText("Chosen")).toBeInTheDocument();
    expect(within(rowFor("NationalId")).getByText("Platform default")).toBeInTheDocument();
    expect(within(rowFor("NationalId")).getByText(/Nobody has chosen this/i)).toBeInTheDocument();
  });

  it("refuses to empty the warning thresholds", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "adminSetDocumentValidity");
    render(api);
    await screen.findByText("PractitionerLicence");

    const row = within(rowFor("PractitionerLicence"));
    await user.clear(row.getByLabelText(/Warn at/i));

    // Clearing the field would silence an expiring licence completely, and nothing on any screen would report
    // it — the failure looks exactly like a quiet week.
    expect(await screen.findByText(/would silence an expiring document completely/i)).toBeInTheDocument();
    expect(row.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("refuses a threshold that is not a whole number, with its own message", async () => {
    const user = userEvent.setup();
    render();
    await screen.findByText("PractitionerLicence");

    const row = within(rowFor("PractitionerLicence"));
    await user.clear(row.getByLabelText(/Warn at/i));
    await user.type(row.getByLabelText(/Warn at/i), "90,soon,30");

    // A DIFFERENT message from the empty case. One string for both would send a supervisor to fix the wrong
    // thing — an empty list is a silenced document, a bad token is a typo.
    expect(await screen.findByText(/Whole numbers between/i)).toBeInTheDocument();
    expect(screen.queryByText(/would silence an expiring document completely/i)).not.toBeInTheDocument();
  });

  it("refuses a renewal period outside the server's bounds", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "adminSetDocumentValidity");
    render(api);
    await screen.findByText("NationalId");

    const row = within(rowFor("NationalId"));
    await user.clear(row.getByLabelText(/Renewal period/i));
    await user.type(row.getByLabelText(/Renewal period/i), "36500");

    // The bounds come from the SERVER, so the screen and the endpoint cannot disagree about them.
    expect(row.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(spy).not.toHaveBeenCalled();
  });

  it("sends only what changed", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "adminSetDocumentValidity");
    render(api);
    await screen.findByText("NationalId");

    const row = within(rowFor("NationalId"));
    await user.clear(row.getByLabelText(/Renewal period/i));
    await user.type(row.getByLabelText(/Renewal period/i), "540");
    await user.click(row.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(spy).toHaveBeenCalled());
    const sent = spy.mock.calls[spy.mock.calls.length - 1][0];
    expect(sent.kind).toBe("NationalId");
    expect(sent.days).toBe(540);
    // The untouched thresholds are NOT sent. Writing them anyway would put this supervisor's name on a
    // decision they did not make.
    expect(sent.warnDays).toBeUndefined();
  });

  it("does not offer to save when nothing has changed", async () => {
    render();
    await screen.findByText("RefugeeId");
    // Saving an unchanged row would record a decision that changed nothing, which makes the change history
    // harder to read for no gain.
    expect(within(rowFor("RefugeeId")).getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("has no serious or critical a11y violations", async () => {
    const { container } = render();
    await screen.findByText("RefugeeId");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
