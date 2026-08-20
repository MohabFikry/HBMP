import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { MemberClinicalPanel } from "../src/screens/encounter/MemberClinicalPanel";
import type { ApiClient } from "../src/api/client";
import { seedSession } from "./helpers";

/**
 * 32.2 — the current-medications section.
 *
 * ============================================================================================================
 * WHY THIS SCREEN EXISTS AT ALL
 * ============================================================================================================
 * `medication_history` has been a table since phase 4.1 with a POST nothing ever called. It fed `/clinical`'s
 * medication list and the FHIR MedicationStatement projection with nothing, so both reported "no medications"
 * as a fact about every patient on the platform.
 *
 * Since 32.1 it is also half of the prescribing interaction check's input — and the half that cannot be
 * derived from Mersal's own data, because `SelfReported` and `External` are by definition medicines Mersal
 * did not prescribe. Without this control the union has one arm.
 *
 * ============================================================================================================
 * THE DISTINCTION EVERY ASSERTION HERE PROTECTS
 * ============================================================================================================
 * "Nothing recorded" is not "takes nothing". The panel this lives in already holds that line for allergies,
 * in those words, and the reason is identical: an empty list rendered as a calm blank tells a prescriber the
 * second sentence when only the first is true.
 */

function renderPanel(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <MemberClinicalPanel beneficiaryId="b-1" />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("Current medications (32.2)", () => {
  it("lists what the patient is already taking, and says where each fact came from", async () => {
    renderPanel();

    const list = await screen.findByRole("list", { name: /current medications/i });
    const rows = within(list).getAllByRole("listitem");

    expect(rows).toHaveLength(2);
    // The source is on the screen because the interaction warning names it: a prescriber deciding what to do
    // is entitled to know whether the warning rests on a dispensing record or on a recollection.
    expect(within(list).getByText(/warfarin/i)).toBeInTheDocument();
    expect(within(list).getByText(/prescribed/i)).toBeInTheDocument();
    expect(within(list).getByText(/st john's wort/i)).toBeInTheDocument();
    expect(within(list).getByText(/self-reported/i)).toBeInTheDocument();
  });

  it("records a medicine the patient is already on", async () => {
    const user = userEvent.setup();
    renderPanel();
    await screen.findByRole("list", { name: /current medications/i });

    await user.click(screen.getByRole("button", { name: /add medication/i }));
    await user.type(screen.getByLabelText(/medicine/i), "Glucophage");
    await user.click(await screen.findByRole("option", { name: /glucophage/i }));
    await user.click(screen.getByRole("combobox", { name: /source/i }));
    await user.click(await screen.findByRole("option", { name: /self-reported/i }));
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    const list = await screen.findByRole("list", { name: /current medications/i });
    await waitFor(() => expect(within(list).getByText(/glucophage/i)).toBeInTheDocument());
  });

  it("stops a medication without deleting it, and it leaves the active list", async () => {
    const user = userEvent.setup();
    renderPanel();

    const list = await screen.findByRole("list", { name: /current medications/i });
    const row = within(list).getByRole("listitem", { name: /warfarin/i });
    await user.click(within(row).getByRole("button", { name: /stopped/i }));

    await waitFor(() =>
      expect(within(screen.getByRole("list", { name: /current medications/i }))
        .queryByText(/warfarin/i)).not.toBeInTheDocument());
  });

  it("renders an empty list as 'nothing recorded', never as 'takes nothing'", async () => {
    // Object.assign onto the instance, not a spread of it. `{...client}` copies OWN properties and leaves
    // every prototype method behind, so the fake answers this one call and throws on all the others — which
    // renders as a failed panel and looks exactly like the assertion failing.
    const api = Object.assign(new DevApiClient({ latencyMs: 0 }), {
      medicationHistory: vi.fn().mockResolvedValue([]),
    }) as unknown as ApiClient;
    renderPanel(api);

    expect(await screen.findByText(/no medications recorded/i)).toBeInTheDocument();
    expect(screen.queryByText(/takes no medication/i)).not.toBeInTheDocument();
  });

  it("does not report an outage as an empty medication list", async () => {
    const api = Object.assign(new DevApiClient({ latencyMs: 0 }), {
      medicationHistory: vi.fn().mockRejectedValue(new Error("emr unreachable")),
    }) as unknown as ApiClient;
    renderPanel(api);

    // Same rule the allergy half of this panel already holds: a failed read is not an empty record, and
    // rendering the normal empty state would turn an outage into a clinical statement.
    expect(await screen.findByText(/could not be loaded/i)).toBeInTheDocument();
    expect(screen.queryByText(/no medications recorded/i)).not.toBeInTheDocument();
  });
});
