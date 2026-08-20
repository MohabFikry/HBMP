import { describe, expect, it, beforeEach, vi, afterEach } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient, DEV_SESSION_KEY } from "../src/auth/devAuthClient";
import { ApiProvider } from "../src/api/ApiProvider";
import { DevApiClient } from "../src/api/DevApiClient";
import { FinanceSettlements, FinanceUtilization, FinanceExports } from "../src/screens/FinancePortal";

/**
 * The finance portal and the pharmacy counter, RENDERED.
 *
 * Every defect this pass fixed was about what a screen can SAY or REACH — a lifecycle with no button, four
 * states with one chip, an export that produced no file, a price nobody could see, a shortage nobody could
 * report. A test asserting markup would have passed throughout, which is why the previous ones did.
 *
 * The client-layer regressions live in `http-client-contract.test.ts`; this file is the screens.
 */
const wrap = (ui: React.ReactNode, api?: DevApiClient) =>
  render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        {api ? <ApiProvider client={api}>{ui}</ApiProvider> : ui}
      </MemoryRouter>
    </AppProviders>,
  );

/** Sign in as the dev finance officer, whose id the settlement fixtures name as a submitter. */
function signInAsFinance() {
  localStorage.setItem(DEV_SESSION_KEY, JSON.stringify({
    userId: "dev-finance",
    displayName: "Dev Finance",
    role: "finance",
    roles: ["finance"],
    expiresAt: Date.now() + 60 * 60 * 1000,
  }));
}

beforeEach(() => { localStorage.clear(); sessionStorage.clear(); });

// ---------------------------------------------------------------------------------------------------------
describe("Provider settlements — the lifecycle that had no door", () => {
  it("offers Submit on a draft, which no screen in this application could do", async () => {
    // `finance` has held `finance:write` and `finance:approve` since phase 10.2 and the portal had a table
    // and a "View lines" button. A settlement could only exist if something outside the product put it there.
    signInAsFinance();
    wrap(<FinanceSettlements />, new DevApiClient());
    await waitFor(() => expect(screen.getByText("STL-2026-000005")).toBeTruthy());
    expect(screen.getByRole("button", { name: /submit for approval/i })).toBeTruthy();
  });

  it("has a way to generate one at all", async () => {
    signInAsFinance();
    wrap(<FinanceSettlements />, new DevApiClient());
    const form = await screen.findByRole("form", { name: /generate a settlement/i });
    expect(within(form).getByRole("button", { name: /generate draft/i })).toBeTruthy();
  });

  it("gives each of the four states its own chip, where all four were the same green", async () => {
    // The literal `status: "ok"` in the client made Draft, Submitted, Approved and Paid visually identical —
    // and, being a string against an object schema, threw before anyone could notice.
    signInAsFinance();
    wrap(<FinanceSettlements />, new DevApiClient());
    await waitFor(() => expect(screen.getByText("STL-2026-000005")).toBeTruthy());
    const grid = screen.getByRole("grid");
    expect(within(grid).getAllByText(/^Draft$/).length).toBeGreaterThan(0);
    expect(within(grid).getAllByText(/^Submitted$/).length).toBeGreaterThan(0);
    expect(within(grid).getAllByText(/^Approved$/).length).toBeGreaterThan(0);
  });

  it("withholds Approve from the person who submitted, and says why", async () => {
    // SoD BEFORE the click (design 49 §3.1). The service refuses with 409 `urn:hbmp:sod-violation` and that
    // refusal stays — but a screen that offers the button and then refuses it is a control working correctly
    // and reading as a defect. STL-2026-000007 is submitted by `dev-finance`, who is signed in.
    signInAsFinance();
    wrap(<FinanceSettlements />, new DevApiClient());
    await waitFor(() => expect(screen.getByText("STL-2026-000007")).toBeTruthy());
    expect(screen.getByText(/you submitted this settlement, so somebody else has to approve it/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^approve$/i })).toBeNull();
  });

  it("shows which lines have no contract tariff, on the screen where the money is authorised", async () => {
    // `PriceSource` is projected by the service with a comment saying a reviewer has to be able to tell a
    // contract price from an inferred floor. The client dropped it, so they were rendered identically.
    signInAsFinance();
    wrap(<FinanceSettlements />, new DevApiClient());
    const user = userEvent.setup();
    await waitFor(() => expect(screen.getByText("STL-2026-000005")).toBeTruthy());
    const row = screen.getByText("STL-2026-000005").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /view lines/i }));

    await waitFor(() => expect(screen.getByText(/observed floor/i)).toBeTruthy());
    // And the count is stated rather than left to be found by reading down a column.
    expect(screen.getByText(/1 of these lines have no contract tariff/i)).toBeTruthy();
  });

  it("filters by state on the SERVER rather than in the browser", async () => {
    signInAsFinance();
    const api = new DevApiClient();
    const spy = vi.spyOn(api, "settlements");
    wrap(<FinanceSettlements />, api);
    const user = userEvent.setup();
    await waitFor(() => expect(screen.getByText("STL-2026-000005")).toBeTruthy());

    await user.click(screen.getByRole("radio", { name: /^Draft$/i }));
    // The browser cannot see past the endpoint's 100-row cap, so filtering there filtered a truncated set
    // while presenting it as complete.
    await waitFor(() => expect(spy).toHaveBeenCalledWith({ status: "Draft" }));
  });
});

// ---------------------------------------------------------------------------------------------------------
describe("Finance period", () => {
  it("sends a period to utilization, which used to send none", async () => {
    const api = new DevApiClient();
    const spy = vi.spyOn(api, "utilization");
    wrap(<FinanceUtilization />, api);
    await waitFor(() => expect(spy).toHaveBeenCalled());
    const period = spy.mock.calls[0][0];
    // The endpoint has accepted from/to since phase 10.2 and the screen sent neither, so finance saw the
    // trailing month forever and could not close a prior one.
    expect(period?.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(period?.to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it("re-asks the server when the period changes", async () => {
    const api = new DevApiClient();
    const spy = vi.spyOn(api, "utilization");
    wrap(<FinanceUtilization />, api);
    const user = userEvent.setup();
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole("radio", { name: /this quarter/i }));
    // Re-asked, not re-filtered: the browser is holding one month and cannot narrow its way to a quarter.
    await waitFor(() => expect(spy.mock.calls.length).toBeGreaterThan(1));
    const last = spy.mock.calls[spy.mock.calls.length - 1];
    expect(last[0]!.from).not.toBe(spy.mock.calls[0][0]!.from);
  });
});

// ---------------------------------------------------------------------------------------------------------
describe("Exports", () => {
  afterEach(() => { vi.restoreAllMocks(); });

  it("no longer offers a format the server has never produced", async () => {
    wrap(<FinanceExports />, new DevApiClient());
    await screen.findByRole("button", { name: /export/i });
    // XLSX was a control the operator believed they had used. The endpoint always returned CSV and stored
    // the CLAIMED format in the export ledger.
    expect(screen.queryByRole("radio", { name: /xlsx/i })).toBeNull();
    expect(screen.getByText(/CSV — opens in Excel/i)).toBeTruthy();
  });

  it("asks for the report the operator selected", async () => {
    const api = new DevApiClient();
    const spy = vi.spyOn(api, "exportReport");
    vi.spyOn(window, "confirm").mockReturnValue(true);
    wrap(<FinanceExports />, api);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("radio", { name: /provider settlements|settlement/i }));
    await user.click(screen.getByRole("button", { name: /export/i }));

    // The server used to run the utilization query whatever this said, and name the file — and the
    // high-severity audit event — after the report nobody got.
    await waitFor(() => expect(spy).toHaveBeenCalled());
    expect(spy.mock.calls[0][0].report).toBe("settlement");
    expect(spy.mock.calls[0][0].format).toBe("csv");
  });

  it("sends the portal's period rather than a window the operator retypes", async () => {
    const api = new DevApiClient();
    const spy = vi.spyOn(api, "exportReport");
    vi.spyOn(window, "confirm").mockReturnValue(true);
    wrap(<FinanceExports />, api);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /export/i }));
    await waitFor(() => expect(spy).toHaveBeenCalled());
    expect(spy.mock.calls[0][0].from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});
