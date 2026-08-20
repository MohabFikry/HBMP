import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { LineNotesPanel } from "../src/screens/notes/LineNotesPanel";
import type { ApiClient } from "../src/api/client";
import { seedSession } from "./helpers";

/**
 * 32.5 — notes on an order or prescription line (design 46 §7b).
 *
 * ============================================================================================================
 * THE FEATURE THAT EXISTED ON THE SERVER AND NOWHERE ELSE
 * ============================================================================================================
 * orders-service has served line notes since 30.5b — read, write, cancel, three visibility classes,
 * sensitivity inherited from the line. `HttpApiClient` had no note method of any kind, so every part of it
 * was unreachable: doc 46 §7b's whole section shipped and was invisible.
 *
 * The doc's own test of whether this is done is quoted in the second case below: "Notes appear on the line in
 * the doctor's view, PROMINENTLY IN THE FULFILLER'S QUEUE DETAIL (an instruction nobody reads is worthless)".
 * Wiring the write side alone would have built precisely the worthless instruction it names.
 */

function renderPanel(props: Partial<Parameters<typeof LineNotesPanel>[0]> = {}, api?: ApiClient) {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api ?? new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <LineNotesPanel kind="prescription" orderId="rx-1" lineId="line-1" {...props} />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("Line notes (32.5)", () => {
  it("sends an instruction with the order", async () => {
    const user = userEvent.setup();
    const writeLineNote = vi.fn().mockResolvedValue({
      noteId: "n-9", lineId: "line-1", visibility: "ToFulfiller", body: "Fasting sample please.",
      authorDisplayName: "Dr Karim Adel", authoredAt: "2026-08-20T10:00:00Z", status: "Active",
      cancelledAt: null, cancelReason: null,
    });
    renderPanel({}, Object.assign(new DevApiClient({ latencyMs: 0 }), { writeLineNote }) as unknown as ApiClient);

    await screen.findByRole("region", { name: /notes/i });
    await user.click(screen.getByRole("button", { name: /add note/i }));
    await user.type(screen.getByLabelText(/^note$/i), "Fasting sample please.");
    await user.click(screen.getByRole("button", { name: /save note/i }));

    await waitFor(() => expect(writeLineNote).toHaveBeenCalledWith(
      "prescription", "rx-1", "line-1", "Fasting sample please.", "ToFulfiller"));
  });

  it("shows the prescriber's instruction to the counter", async () => {
    // The fixture seeds one ToFulfiller note on a prescription line. This is the half doc 46 calls the
    // point of the feature.
    renderPanel({ asFulfiller: true });

    const panel = await screen.findByRole("region", { name: /notes/i });
    expect(await within(panel).findByText(/cannot swallow tablets/i)).toBeInTheDocument();
  });

  it("offers a fulfiller only the class they may write", async () => {
    const user = userEvent.setup();
    renderPanel({ asFulfiller: true });

    await screen.findByRole("region", { name: /notes/i });
    await user.click(screen.getByRole("button", { name: /add note/i }));
    await user.click(screen.getByRole("combobox", { name: /who can read this/i }));

    const options = await screen.findAllByRole("option");
    // Letting a lab or a pharmacy write ToFulfiller would put words in the ordering clinician's mouth. The
    // server returns 403 provider-note-class for it; offering it and then refusing would be a worse screen.
    expect(options.map((o) => o.textContent)).toEqual(["Reply to the ordering clinician"]);
  });

  it("keeps a withdrawn note visible, struck through, with the reason", async () => {
    const user = userEvent.setup();
    renderPanel({ asFulfiller: false });

    const panel = await screen.findByRole("region", { name: /notes/i });
    await within(panel).findByText(/cannot swallow tablets/i);
    await user.click(within(panel).getByRole("button", { name: /withdraw/i }));
    await user.type(screen.getByLabelText(/why is it being withdrawn/i), "Wrong line.");
    await user.click(within(panel).getAllByRole("button", { name: /^withdraw$/i })[0]);

    // "There was a note here and it was withdrawn, by X, because Z" is information; a gap is not.
    await waitFor(() => expect(within(panel).getByText(/withdrawn: wrong line\./i)).toBeInTheDocument());
    expect(within(panel).getByText(/cannot swallow tablets/i)).toBeInTheDocument();
  });

  it("refuses a withdrawal with no reason", async () => {
    const user = userEvent.setup();
    renderPanel();

    const panel = await screen.findByRole("region", { name: /notes/i });
    await within(panel).findByText(/cannot swallow tablets/i);
    await user.click(within(panel).getByRole("button", { name: /withdraw/i }));
    await user.click(within(panel).getAllByRole("button", { name: /^withdraw$/i })[0]);

    expect(await screen.findByRole("alert")).toHaveTextContent(/reason is required/i);
  });

  it("does not report an outage as an empty note list", async () => {
    const api = Object.assign(new DevApiClient({ latencyMs: 0 }), {
      lineNotes: vi.fn().mockRejectedValue(new Error("orders unreachable")),
    }) as unknown as ApiClient;
    renderPanel({}, api);

    // A pharmacist about to fill a line must not be told there is no instruction when there may be one.
    expect(await screen.findByText(/could not be loaded/i)).toBeInTheDocument();
    expect(screen.queryByText(/no notes on this line/i)).not.toBeInTheDocument();
  });
});
