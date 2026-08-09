import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { seedSession } from "./helpers";

/**
 * 29.5 (design 45 §5) — the acute/chronic toggle, the frequency combobox, and the schedule the doctor sees
 * BEFORE submitting.
 *
 * <p>Gate 5 asks for all three in as many words. None existed: `PrescribingWorkspace` had no toggle, no
 * frequency field and no preview, so the wired server endpoint was unreachable and every prescription the
 * SPA wrote took the server's `DEFAULT 'Acute'`.</p>
 *
 * <p><b>The preview is the safety-relevant one.</b> A chronic script commits a patient's benefit across
 * months. Showing the per-window quantities before submit is what lets a prescriber notice that 34/33/33 is
 * not what they meant while it is still free to change.</p>
 */
function renderEncounter() {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter
        initialEntries={["/clinician/encounter?encounter=ENC-2026-000231"]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <AppRouter />
      </MemoryRouter>
    </AppProviders>,
  );
}

/*
 * These queries run against the whole document rather than a captured pane handle, deliberately.
 *
 * Radix re-mounts a panel subtree when the composer's state changes, so a pane node held across an
 * interaction goes DETACHED — and a detached `<label>` still matches by text while `label.control` returns
 * null, which surfaces as "no form control was found associated to that label" and reads exactly like a
 * missing `for` attribute in the component. It is not; it is a stale handle.
 *
 * Scoping is safe here because every control this file asserts on — the kind radios, the refill frequency,
 * the treatment duration, the schedule — exists only in the prescribing composer, so there is nothing in a
 * sibling pane for an unscoped query to match by accident.
 */

async function openPrescriptions() {
  const user = userEvent.setup();
  renderEncounter();
  await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
  const outer = (await screen.findAllByRole("tablist"))[0];
  await user.click(await within(outer).findByRole("tab", { name: /prescriptions/i }));
  return user;
}

/**
 * Set the script's treatment length — which, since 31.1, means setting THE LINE's duration.
 *
 * <p>There used to be a second field for this above the composer, so one fact had two places to be stated
 * and the schedule was built from whichever the doctor filled in second. The script's length is now derived
 * from the longest line, which is the only place it was ever really recorded — so a medicine has to be
 * chosen before there is a line to record it on.</p>
 */
async function setTreatmentDuration(user: ReturnType<typeof userEvent.setup>, days: string) {
  const combo = await screen.findByRole("combobox", { name: /medicine|الدواء/i });
  await user.type(combo, "me");
  const list = await screen.findByRole("listbox", { name: /medicine|الدواء/i }, { timeout: 5000 });
  await user.click(within(list).getAllByRole("option")[0]);

  const duration = await screen.findByRole("spinbutton", { name: /duration|المدة/i });
  await user.clear(duration);
  await user.type(duration, days);
}

describe("29.5 — the acute/chronic toggle", () => {
  it("offers Acute and Chronic on the prescriptions composer", async () => {
    await openPrescriptions();

    expect(await screen.findByRole("radio", { name: /acute|حادة/i })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /chronic|مزمنة/i })).toBeInTheDocument();
  });

  it("starts on Acute, which is today's behaviour unchanged", async () => {
    await openPrescriptions();

    expect(await screen.findByRole("radio", { name: /acute|حادة/i })).toBeChecked();
  });

  it("hides the refill frequency until Chronic is chosen", async () => {
    await openPrescriptions();

    expect(screen.queryByLabelText(/refill frequency|تكرار الصرف/i)).not.toBeInTheDocument();
  });

  it("reveals the refill frequency when Chronic is chosen", async () => {
    const user = await openPrescriptions();

    await user.click(await screen.findByRole("radio", { name: /chronic|مزمنة/i }));

    expect(await screen.findByLabelText(/refill frequency|تكرار الصرف/i)).toBeInTheDocument();
  });

  it("offers the master-table cadences and not a hardcoded list", async () => {
    const user = await openPrescriptions();

    await user.click(await screen.findByRole("radio", { name: /chronic|مزمنة/i }));
    const select = await screen.findByLabelText(/refill frequency|تكرار الصرف/i);

    expect(within(select).getByRole("option", { name: /monthly/i })).toBeInTheDocument();
    expect(within(select).getByRole("option", { name: /every 3 months/i })).toBeInTheDocument();
    // Seeded INACTIVE — offering it would compose a script the write path refuses.
    expect(within(select).queryByRole("option", { name: /every 6 months/i })).not.toBeInTheDocument();
  });
});

describe("29.5 — the schedule the doctor sees before submitting", () => {
  it("shows the per-window quantities for a chronic script", async () => {
    const user = await openPrescriptions();

    await user.click(await screen.findByRole("radio", { name: /chronic|مزمنة/i }));
    await user.selectOptions(await screen.findByLabelText(/refill frequency|تكرار الصرف/i), "Monthly");
    await setTreatmentDuration(user, "90");

    // Three collections, and the schedule says so in dates and quantities rather than in prose.
    const schedule = await screen.findByTestId("chronic-schedule", {}, { timeout: 3000 });
    expect(within(schedule).getAllByRole("row").length).toBeGreaterThanOrEqual(3);
  });

  it("states the total the windows sum to, so a mismatch is visible", async () => {
    // Invariant 5 — the allocation sums EXACTLY to the prescribed total. Showing the total beside the
    // windows is what makes that checkable by the person who signs it.
    const user = await openPrescriptions();

    await user.click(await screen.findByRole("radio", { name: /chronic|مزمنة/i }));
    await user.selectOptions(await screen.findByLabelText(/refill frequency|تكرار الصرف/i), "Monthly");
    await setTreatmentDuration(user, "90");

    // The row header specifically, not the prose beneath the table — which also says "total" and would let
    // this pass with the figure itself missing.
    const schedule = await screen.findByTestId("chronic-schedule", {}, { timeout: 3000 });
    const totalRow = within(schedule).getByRole("rowheader", { name: /total|الإجمالي/i });
    expect(totalRow).toBeInTheDocument();
    expect(Number(totalRow.parentElement?.querySelector("td")?.textContent)).toBeGreaterThan(0);
  });

  it("refuses a duration of one month or less with a clear message", async () => {
    // "A 14-day course is not chronic — reject with a clear message, do not silently accept."
    const user = await openPrescriptions();

    await user.click(await screen.findByRole("radio", { name: /chronic|مزمنة/i }));
    await user.selectOptions(await screen.findByLabelText(/refill frequency|تكرار الصرف/i), "Monthly");
    await setTreatmentDuration(user, "14");

    expect(await screen.findByText(/more than one month|أكثر من شهر/i)).toBeInTheDocument();
  });
});
