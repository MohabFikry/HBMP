import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { seedSession } from "./helpers";

/**
 * 29.2 (design 45 §2, invariant 3) — the OP Procedures composer, routing a code to its vehicle.
 *
 * <p>"The doctor picks a service; the SYSTEM decides the vehicle." The composer must therefore SAY which
 * vehicle a chosen code takes, before the doctor commits — that is the stated purpose of
 * `/orderable-services`, which had no caller at all — and then actually create that thing on submit.</p>
 *
 * <p>Queries run against the document rather than a captured pane handle: the panel subtree re-mounts as the
 * composer's state changes, and a detached node still matches by text while its controls resolve to null.</p>
 */
function renderEncounter(api?: Partial<ApiClient>) {
  seedSession("doctor");
  const dev = new DevApiClient({ latencyMs: 0 });
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={Object.assign(dev, api) as ApiClient}>
      <MemoryRouter
        initialEntries={["/clinician/encounter?encounter=ENC-2026-000231"]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <AppRouter />
      </MemoryRouter>
    </AppProviders>,
  );
}

async function openProcedures(api?: Partial<ApiClient>) {
  const user = userEvent.setup();
  renderEncounter(api);
  await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
  const outer = (await screen.findAllByRole("tablist"))[0];
  await user.click(await within(outer).findByRole("tab", { name: /op procedures/i }));
  return user;
}

/**
 * Picks the first service the CPT combobox offers for a query.
 *
 * <p>Scoped to the combobox's own LISTBOX. The procedure-type field is a native `<select>`, whose entries
 * are also `option`s, so an unscoped query is ambiguous — and would have been satisfied by whichever the
 * DOM happened to order first.</p>
 */
async function chooseService(user: ReturnType<typeof userEvent.setup>, query: string) {
  const cpt = screen.getByPlaceholderText(/CPT code or name/i);
  await user.type(cpt, query);
  const listbox = await screen.findByRole("listbox", {}, { timeout: 3000 });
  await user.click(within(listbox).getAllByRole("option")[0]);
}

describe("29.2 — the composer says what a code will create", () => {
  it("marks an E/M code as a referral before the doctor commits", async () => {
    const user = await openProcedures({
      searchCpt: vi.fn().mockResolvedValue([{ code: "99243", description: "Office consultation" }]),
      orderableServices: vi.fn().mockResolvedValue([{
        code: "99243", description: "Office consultation", section: "EvaluationAndManagement",
        vehicle: "Referral", orderable: true, reason: null,
      }]),
    });

    await chooseService(user, "99243");

    // In words the doctor reads, not an enum. A referral is a different thing from an order, and the
    // difference — a loop that must be closed with a report — is the point of saying so here.
    //
    // Matched exactly, because "Referred to (specialty)" and the explanatory hint both contain the word:
    // a loose matcher would pass even if the chip itself never rendered.
    expect(await screen.findByText(/^referral$/i, {}, { timeout: 3000 })).toBeInTheDocument();
  });

  it("marks a surgery code as a procedure order", async () => {
    const user = await openProcedures({
      searchCpt: vi.fn().mockResolvedValue([{ code: "29881", description: "Knee arthroscopy" }]),
      orderableServices: vi.fn().mockResolvedValue([{
        code: "29881", description: "Knee arthroscopy", section: "Surgery",
        vehicle: "ProcedureOrder", orderable: true, reason: null,
      }]),
    });

    await chooseService(user, "29881");

    expect(await screen.findByText(/procedure order/i, {}, { timeout: 3000 })).toBeInTheDocument();
  });
});

describe("29.2 — an E/M code raises a referral, not an order", () => {
  it("calls createReferral and NOT submitInvestigationOrder", async () => {
    const createReferral = vi.fn().mockResolvedValue({
      referralId: "r-1", referralNo: "REF-2026-000001", status: "Requested", requestedServiceCode: "99243",
    });
    const submitInvestigationOrder = vi.fn();

    const user = await openProcedures({
      searchCpt: vi.fn().mockResolvedValue([{ code: "99243", description: "Office consultation" }]),
      orderableServices: vi.fn().mockResolvedValue([{
        code: "99243", description: "Office consultation", section: "EvaluationAndManagement",
        vehicle: "Referral", orderable: true, reason: null,
      }]),
      createReferral,
      submitInvestigationOrder,
    });

    await chooseService(user, "99243");
    await user.type(await screen.findByLabelText(/specialty|التخصص/i), "Cardiology");
    await user.click(await screen.findByRole("button", { name: /check|تحقّق/i }));
    await user.click(await screen.findByRole("button", { name: /send|refer|إرسال/i }));

    expect(createReferral).toHaveBeenCalledTimes(1);
    expect(createReferral.mock.calls[0][0]).toMatchObject({
      requestedServiceCode: "99243",
      targetSpecialty: "Cardiology",
    });
    // The whole invariant, as an assertion: an E/M code must NOT travel the order path.
    expect(submitInvestigationOrder).not.toHaveBeenCalled();
  });

  it("asks for the target specialty, because a referral without one cannot be routed", async () => {
    // pharmacy refuses `missing-specialty` 422. A field the doctor can see is unfilled beats a rejection
    // they have to interpret.
    const user = await openProcedures({
      searchCpt: vi.fn().mockResolvedValue([{ code: "99243", description: "Office consultation" }]),
      orderableServices: vi.fn().mockResolvedValue([{
        code: "99243", description: "Office consultation", section: "EvaluationAndManagement",
        vehicle: "Referral", orderable: true, reason: null,
      }]),
    });

    await chooseService(user, "99243");

    expect(await screen.findByLabelText(/specialty|التخصص/i)).toBeInTheDocument();
  });

  it("does not ask for a specialty on a procedure-order line", async () => {
    const user = await openProcedures({
      searchCpt: vi.fn().mockResolvedValue([{ code: "29881", description: "Knee arthroscopy" }]),
      orderableServices: vi.fn().mockResolvedValue([{
        code: "29881", description: "Knee arthroscopy", section: "Surgery",
        vehicle: "ProcedureOrder", orderable: true, reason: null,
      }]),
    });

    await chooseService(user, "29881");
    await screen.findByText(/procedure order/i, {}, { timeout: 3000 });

    expect(screen.queryByLabelText(/specialty|التخصص/i)).not.toBeInTheDocument();
  });
});
