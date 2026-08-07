import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { PrescribingWorkspace } from "../src/screens/prescribing/PrescribingWorkspace";

/**
 * Interactions and dosing read live from manufacturer labels (openFDA), alongside the curated sources.
 *
 * <p>The rendering problem this guards is created by the feature itself: a line now carries TWO Interaction
 * findings, from two independent sources with different authority. Rendered naively that shows the prescriber
 * "Interactions" twice with different verdicts and no way to tell which is which, and puts two
 * acknowledgement boxes on screen bound to a single stored reason — so typing in one silently overwrites the
 * other, and the prescription is submitted carrying a reason the prescriber did not write.</p>
 */

function renderWorkspace() {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PrescribingWorkspace encounterId="enc-77" diagnosisIcdCodes={["E11.9"]} />
      </MemoryRouter>
    </AppProviders>,
  );
}

async function pickDrug(user: ReturnType<typeof userEvent.setup>, query: string, nth = 0) {
  const boxes = screen.getAllByRole("combobox");
  await user.type(boxes[nth], query);
  const options = await screen.findAllByRole("option");
  await user.click(options[0]);
}

/**
 * Two medicines whose labels reference one another, validated, with the first line's checks open.
 *
 * An interaction needs two drugs; a single-drug prescription has no pair for the label scan to answer about,
 * and the check correctly says nothing at all in that case.
 */
async function openChecksOnTwoDrugPrescription(user: ReturnType<typeof userEvent.setup>, line = 0) {
  renderWorkspace();

  await pickDrug(user, "augmentin");
  await user.click(screen.getByRole("button", { name: /add medicine|إضافة دواء/i }));
  // Index 0 again: choosing a drug REPLACES its combobox with a "Change medicine" button, so the new line's
  // box is the only one on screen.
  await pickDrug(user, "glucophage");
  await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

  await waitFor(() =>
    expect(screen.getAllByRole("button").filter((b) => b.className.includes("rx-status--button")))
      .not.toHaveLength(0));

  const chips = screen.getAllByRole("button").filter((b) => b.className.includes("rx-status--button"));
  await user.click(chips[line]);
  return screen.findByRole("dialog");
}

describe("the manufacturer-label checks", () => {
  it("shows the interaction check once, not once per source", async () => {
    const user = userEvent.setup();
    await openChecksOnTwoDrugPrescription(user);

    // One row for the check, carrying both sources' messages beneath it. Two rows headed "Interaction"
    // with different chips is not a report, it is a puzzle. Scoped to the checks list, because the collapsed
    // sources disclosure below it legitimately names the check once per source.
    expect(within(document.querySelector(".rx-checks")!).getAllByText("Interaction")).toHaveLength(1);
  });

  it("quotes the manufacturer's own wording, marked as English", async () => {
    const user = userEvent.setup();
    await openChecksOnTwoDrugPrescription(user);

    const quote = await screen.findByText(/Individualize the dosing regimen/i);
    // A regulatory document reproduced verbatim, not the platform's own words — and rendered ltr so it stays
    // readable inside the Arabic layout instead of mirroring into nonsense.
    expect(quote).toHaveAttribute("dir", "ltr");
    expect(quote).toHaveAttribute("lang", "en");
  });

  it("says the dose was NOT compared with what was prescribed", async () => {
    const user = userEvent.setup();
    await openChecksOnTwoDrugPrescription(user);

    // openFDA publishes dosing as prose with no structured ceiling anywhere in the dataset. Showing the text
    // beside a dose the system did not check is only safe if the screen says which of those it is doing.
    expect(await screen.findByText(/has NOT been compared with what you prescribed/i)).toBeInTheDocument();
  });

  it("never reports the label scan as an all-clear", async () => {
    const user = userEvent.setup();
    const dialog = within(await openChecksOnTwoDrugPrescription(user, 1));

    // The asymmetry the whole feature rests on: a mention is evidence, a silence is not. A green tick here
    // would be an assurance no source ever gave.
    expect(dialog.getByText(/not an all-clear/i)).toBeInTheDocument();
  });

  it("attributes both interaction sources separately", async () => {
    const user = userEvent.setup();
    const dialog = within(await openChecksOnTwoDrugPrescription(user));

    await user.click(dialog.getByText(/sources|المصادر/i));

    // A warning a clinician cannot attribute is one they are right to ignore — and "our curated list" and
    // "the FDA label" carry very different weight, so collapsing them to one line would hide which spoke.
    // Twice over: the label source answers BOTH the interaction check and the dose check, and each entry
    // carries its own caveat rather than one being taken to cover the other.
    expect(dialog.getAllByText(/openFDA drug label/i)).toHaveLength(2);
    expect(dialog.getByText(/Labels are narrative, not a complete interaction list/i)).toBeInTheDocument();
  });

  it("keeps one reason box per check even when two sources warn", async () => {
    const user = userEvent.setup();
    await openChecksOnTwoDrugPrescription(user);

    // Submission is gated per (line, check) server-side. Two inputs bound to one stored record would
    // overwrite each other as they were typed, and the prescriber would not see it happen.
    const interactionRow = within(document.querySelector(".rx-checks")!)
      .getByText("Interaction").closest("li")!;
    expect(within(interactionRow).queryAllByRole("textbox").length).toBeLessThanOrEqual(1);
  });
});
