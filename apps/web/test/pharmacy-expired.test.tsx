import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ApiError } from "../src/api/http";
import { PharmacyDispense } from "../src/screens/PharmacyDispense";

/**
 * An expired prescription at the counter.
 *
 * <p>The two failures this guards against are opposite in shape and equally bad. One is HIDING it: the
 * search used to filter expired prescriptions out, so a pharmacist was told the member has nothing when in
 * fact they have something that has run out of date — a true statement ("nothing dispensable") standing in
 * for a false one ("nothing"). The other is offering to DISPENSE it: the sweeper runs hourly, so a lapsed
 * prescription's status still reads Approved for up to an hour, and a screen trusting that would send a
 * pharmacist into a refusal the row already knew about.</p>
 *
 * <p>The recovery is the third thing being tested. The patient is standing there; sending them back to a
 * doctor for a fresh prescription is a wasted journey and often a second appointment. Asking the approval
 * team is two minutes, and nobody would guess it was available unless the screen said so.</p>
 */

function renderScreen(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PharmacyDispense />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** Search by member number, which the fixture answers with both the live and the expired prescription. */
async function searchMember(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText("Card number"), "MRS-2026-0019");
  await user.type(screen.getByLabelText("Member number"), "MRS-M-10231");
  await user.click(screen.getByRole("button", { name: "Search" }));
}

describe("an expired prescription is shown, not hidden", () => {
  it("lists it alongside the live ones, marked Expired", async () => {
    const user = userEvent.setup();
    renderScreen();
    await searchMember(user);

    // Both come back. The lapsed one is the whole reason the member came to the counter today.
    expect(await screen.findByText("RX-2026-033110")).toBeInTheDocument();
    expect(await screen.findByText("RX-2026-033044")).toBeInTheDocument();
    expect(screen.getAllByText("Expired").length).toBeGreaterThan(0);
  });

  it("does not offer to dispense it", async () => {
    const user = userEvent.setup();
    renderScreen();
    await searchMember(user);

    // "Review", not "Open". The server refuses to open an expired prescription (409), so an Open button
    // here would put the pharmacist through a failure the row already knew about.
    await user.click(await screen.findByRole("button", { name: "Review" }));

    expect(await screen.findByText(/This prescription has expired/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Dispense selected/i })).toBeNull();
  });

  it("still names the medication that lapsed", async () => {
    const user = userEvent.setup();
    renderScreen();
    await searchMember(user);
    await user.click(await screen.findByRole("button", { name: "Review" }));

    // "An expired prescription" and "the patient's metformin" are different questions, and only the second
    // one tells a pharmacist whether this is worth chasing.
    expect(await screen.findByText(/Metformin 500mg/)).toBeInTheDocument();
  });
});

describe("requesting a validity extension", () => {
  it("refuses to send without a reason", async () => {
    const user = userEvent.setup();
    renderScreen();
    await searchMember(user);
    await user.click(await screen.findByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Request extension" }));

    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByRole("button", { name: "Send request" })).toBeDisabled();

    // Too short is refused as well as empty — an approver with three characters in front of them is
    // deciding on who asked, not on why.
    await user.type(dialog.getByRole("textbox"), "pls");
    expect(dialog.getByRole("button", { name: "Send request" })).toBeDisabled();
  });

  it("sends the reason and says the prescription is still expired", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const request = vi.spyOn(api, "requestValidityExtension");
    renderScreen(api as unknown as ApiClient);

    await searchMember(user);
    await user.click(await screen.findByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Request extension" }));

    const dialog = within(await screen.findByRole("dialog"));
    await user.type(dialog.getByRole("textbox"), "Patient is mid-course and could not travel in time.");
    await user.click(dialog.getByRole("button", { name: "Send request" }));

    await waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    expect(request.mock.calls[0][0]).toMatchObject({
      itemType: "Prescription",
      itemReference: "RX-2026-033044",
      reason: "Patient is mid-course and could not travel in time.",
    });

    // The confirmation names the authorization AND says nothing has changed today. "Request sent" reading
    // as "sorted" would have a pharmacist hand over medication that is still not dispensable.
    const outcome = await screen.findByText(/AUTH-2026-000271/);
    expect(outcome.textContent).toMatch(/stays expired until they decide/i);
  });

  it("treats 'already requested' as an answer, not a failure", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { requestValidityExtension: unknown }).requestValidityExtension =
      vi.fn().mockRejectedValue(new ApiError("http", "already open", 409));
    renderScreen(api);

    await searchMember(user);
    await user.click(await screen.findByRole("button", { name: "Review" }));
    await user.click(screen.getByRole("button", { name: "Request extension" }));
    const dialog = within(await screen.findByRole("dialog"));
    await user.type(dialog.getByRole("textbox"), "Patient could not collect before it lapsed.");
    await user.click(dialog.getByRole("button", { name: "Send request" }));

    // A 409 means somebody already asked. Reporting "failed" would send the pharmacist round the loop to
    // raise a duplicate the server refuses anyway.
    expect(await screen.findByText(/already asked for this one/i)).toBeInTheDocument();
  });
});

describe("accessibility", () => {
  it("has no serious or critical violations with an expired prescription open", async () => {
    const user = userEvent.setup();
    const { container } = renderScreen();
    await searchMember(user);
    await user.click(await screen.findByRole("button", { name: "Review" }));
    await screen.findByText(/This prescription has expired/i);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
