import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import ProcedureCentre from "../src/screens/ProcedureCentre";

/**
 * 32.6 — the external delivery centre's counter (design 45 §2b).
 *
 * <p>The service side of this portal had eleven passing tests and the counter's one write had never worked:
 * the screen sent the ORDER id where the server expected a LINE id, because the projection did not carry a
 * line id and nothing on either side compared the two. Every test handed the endpoint ids fetched from the
 * database, so none of them was ever the screen.</p>
 *
 * <p>So these tests assert on the ARGUMENTS the screen passes, not on the answer it gets back. A fake that
 * accepts anything would reproduce the original defect exactly.</p>
 */

function renderScreen(api: ApiClient, mode: "queue" | "counter" = "queue") {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <ProcedureCentre mode={mode} />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("recording a session", () => {
  it("names the LINE, not the order", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const record = vi.fn().mockResolvedValue({
      orderId: "ord-proc-1", orderLineId: "ord-proc-1-line-1",
      sessionsDelivered: 1, sessionsAuthorised: 6, sessionsRemaining: 5,
      progressLabel: "1 of 6 sessions delivered",
    });
    (api as { recordProcedureSession: unknown }).recordProcedureSession = record;

    renderScreen(api);
    await user.click((await screen.findAllByRole("button", { name: "Record session" }))[0]);

    await waitFor(() => expect(record).toHaveBeenCalled());
    const [orderId, orderLineId] = record.mock.calls[0];
    expect(orderId).toBe("ord-proc-1");
    expect(orderLineId).toBe("ord-proc-1-line-1");
    // THE assertion. Passing the order id twice is what the screen used to do, and the server answered 404
    // to every single tap.
    expect(orderLineId).not.toBe(orderId);
  });
});

describe("verifying the person at the counter", () => {
  it("shows the name the directory disclosed", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });

    renderScreen(api as unknown as ApiClient, "counter");
    await user.type(screen.getByLabelText("Card number"), "CARD-123");
    await user.type(screen.getByLabelText("Member number"), "M-9");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    // The section is called "Verify & deliver" and it rendered nothing to verify against: the service passed
    // a null name into a projection whose own contract puts the name on this path.
    expect(await screen.findByText("Amal Hassan")).toBeInTheDocument();
  });

  it("says a withheld name is withheld rather than showing a blank", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const base = new DevApiClient({ latencyMs: 0 });
    (api as { procedureCounterSearch: unknown }).procedureCounterSearch = async () =>
      (await base.procedureCounterSearch({ cardNumber: "CARD-123", memberNo: "M-9" }))
        .map((r) => ({ ...r, beneficiaryDisplayName: null }));

    renderScreen(api, "counter");
    await user.type(screen.getByLabelText("Card number"), "CARD-123");
    await user.type(screen.getByLabelText("Member number"), "M-9");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    // A withheld name is a decision patient-service made about this caller. Rendering "—" would read as a
    // record with no name; rendering anything else would verify the wrong person.
    expect(await screen.findByText("Name not disclosed to your centre")).toBeInTheDocument();
  });
});

describe("closing the referral loop", () => {
  it("sends the report the ordering doctor is waiting for", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const report = vi.fn().mockResolvedValue(undefined);
    (api as { reportProcedureCompletion: unknown }).reportProcedureCompletion = report;

    renderScreen(api);
    await user.click((await screen.findAllByRole("button", { name: "Report back" }))[0]);
    await user.type(
      screen.getByLabelText(/What was found or done/),
      "Six sessions completed; discharged to home exercise.",
    );
    await user.click(screen.getByRole("button", { name: "Send report" }));

    await waitFor(() => expect(report).toHaveBeenCalled());
    expect(report.mock.calls[0][0]).toBe("ord-proc-1");
  });

  it("will not send an empty report", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const report = vi.fn().mockResolvedValue(undefined);
    (api as { reportProcedureCompletion: unknown }).reportProcedureCompletion = report;

    renderScreen(api);
    await user.click((await screen.findAllByRole("button", { name: "Report back" }))[0]);

    // The service refuses an empty body with a typed 422. A client that let one through would turn a
    // deliberate refusal into a failed save nobody can interpret — and an empty report closes the loop
    // without saying anything, which is worse than leaving it open.
    expect(screen.getByRole("button", { name: "Send report" })).toBeDisabled();
    expect(report).not.toHaveBeenCalled();
  });
});
