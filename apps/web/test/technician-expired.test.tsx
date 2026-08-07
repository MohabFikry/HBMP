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
import { LabQueue } from "../src/screens/LabQueue";

/**
 * An expired investigation order at the bench.
 *
 * <p>The mirror of the dispensing counter's expired path, and it guards the same two opposite failures.
 * HIDING it — the queue filtered on Active/PartiallyUsed, so a lapsed order vanished and the technician had
 * an empty list and nothing to tell the patient standing there. And OFFERING it — the expiry sweeper runs
 * hourly, so a lapsed order reads Active for up to an hour, and a Consume button would send the technician
 * into a 409 the row already knew about.</p>
 */

/**
 * The bench is SEARCH-first since 27.8 — it no longer browses every open order in the tenant. Every test here
 * therefore has to ask for a patient before there is anything to assert on, which is the point: reaching one
 * order used to mean putting every other patient's on screen.
 */
async function findPatient(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/Card number/i), "CARD-1");
  await user.type(screen.getByLabelText(/Member number/i), "MEM-1");
  await user.click(screen.getByRole("button", { name: /^Search$/ }));
}

function renderQueue(kind: "lab" | "radiology" = "lab", api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <LabQueue kind={kind} />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("an expired order stays in the queue", () => {
  it("is listed with the live ones, marked Expired", async () => {
    const user = userEvent.setup();
    renderQueue("lab");
    await findPatient(user);

    expect(await screen.findByText("ORD-2026-055012")).toBeInTheDocument();
    // The lapsed one is why the patient is at the bench today, so it is the last thing that should vanish.
    expect(await screen.findByText("ORD-2026-055003")).toBeInTheDocument();
    expect(screen.getAllByText("Expired").length).toBeGreaterThan(0);
  });

  it("offers the extension instead of the order", async () => {
    const user = userEvent.setup();
    renderQueue("lab");
    await findPatient(user);
    await screen.findByText("ORD-2026-055003");

    const row = screen.getByText("ORD-2026-055003").closest("tr")!;
    // Fulfilling it is refused by the server (409 order-expired), so offering the way in would be a promise
    // the screen cannot keep. The recovery takes its place on that row.
    expect(within(row).getByRole("button", { name: "Request extension" })).toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: "Open" })).toBeNull();
  });

  it("still opens the orders that have not lapsed", async () => {
    const user = userEvent.setup();
    renderQueue("lab");
    await findPatient(user);
    await screen.findByText("ORD-2026-055012");

    const row = screen.getByText("ORD-2026-055012").closest("tr")!;
    // Open, not Consume: the modal it replaced could only ever fulfil the order's FIRST line against one
    // panel count, and said nothing about what the patient would be charged (ADR-0034).
    expect(within(row).getByRole("button", { name: "Open" })).toBeInTheDocument();
  });

  it("does the same on the imaging queue", async () => {
    const user = userEvent.setup();
    renderQueue("radiology");
    await findPatient(user);
    await screen.findByText("ORD-2026-077009");

    const row = screen.getByText("ORD-2026-077009").closest("tr")!;
    // The two queues must not diverge: a technician moving between them should not have to learn which one
    // shows lapsed work.
    expect(within(row).getByRole("button", { name: "Request extension" })).toBeInTheDocument();
  });
});

describe("requesting a validity extension", () => {
  it("refuses to send without a reason", async () => {
    const user = userEvent.setup();
    renderQueue("lab");
    await findPatient(user);
    await screen.findByText("ORD-2026-055003");

    const row = screen.getByText("ORD-2026-055003").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Request extension" }));

    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByRole("button", { name: "Send request" })).toBeDisabled();
    await user.type(dialog.getByRole("textbox"), "late");
    expect(dialog.getByRole("button", { name: "Send request" })).toBeDisabled();
  });

  it("sends it against the ORDER, and says the order is still expired", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const request = vi.spyOn(api, "requestValidityExtension");
    renderQueue("lab", api as unknown as ApiClient);
    await findPatient(user);
    await screen.findByText("ORD-2026-055003");

    const row = screen.getByText("ORD-2026-055003").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Request extension" }));
    const dialog = within(await screen.findByRole("dialog"));
    await user.type(dialog.getByRole("textbox"), "Patient travelled today and it lapsed while waiting.");
    await user.click(dialog.getByRole("button", { name: "Send request" }));

    await waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    // InvestigationOrder, not Prescription — the shared modal is told which kind it is holding, and getting
    // that wrong would send the approval team a request against a prescription that does not exist.
    expect(request.mock.calls[0][0]).toMatchObject({
      itemType: "InvestigationOrder",
      itemReference: "ORD-2026-055003",
      reason: "Patient travelled today and it lapsed while waiting.",
    });

    const outcome = await screen.findByText(/AUTH-2026-000271/);
    expect(outcome.textContent).toMatch(/stays expired until they decide/i);
  });

  it("treats 'already requested' as an answer, not a failure", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { requestValidityExtension: unknown }).requestValidityExtension =
      vi.fn().mockRejectedValue(new ApiError("http", "already open", 409));
    renderQueue("lab", api);
    await findPatient(user);
    await screen.findByText("ORD-2026-055003");

    const row = screen.getByText("ORD-2026-055003").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Request extension" }));
    const dialog = within(await screen.findByRole("dialog"));
    await user.type(dialog.getByRole("textbox"), "Patient is here now and the order lapsed yesterday.");
    await user.click(dialog.getByRole("button", { name: "Send request" }));

    expect(await screen.findByText(/already asked for this one/i)).toBeInTheDocument();
  });
});

describe("accessibility", () => {
  it("has no serious or critical violations", async () => {
    const user = userEvent.setup();
    const { container } = renderQueue("lab");
    await findPatient(user);
    await screen.findByText("ORD-2026-055003");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
