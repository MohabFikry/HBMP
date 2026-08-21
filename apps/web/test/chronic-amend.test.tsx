import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AmendLineDialog } from "../src/screens/AmendLineDialog";
import { renderNode } from "./helpers";

/**
 * 32.6 — amending a chronic script's duration and frequency (design 46 §4).
 *
 * ============================================================================================================
 * THE CODE PATH THAT WAS DEBUGGED AND COULD NOT BE REACHED
 * ============================================================================================================
 * `ChronicAmendExecutor` implements the whole of design 46 §4: dispensed windows keep their quantities
 * exactly, the remainder re-allocates by largest-remainder and must still sum precisely, a total below what
 * was collected is refused, and shortening below the chronic definition requires the prescriber's explicit
 * confirmation. 31.5 even FIXED a bug in it — the allocation divided by `pack_size` instead of
 * `pack_content` and would have shown a ninety-day syrup course as 1,800 packs.
 *
 * `POST /{rxId}/lines/{lineId}/amend-schedule` was reached by nothing. `AmendLineDialog` has rendered a
 * `chronicPreview` prop since 30.3, and no caller ever passed one.
 *
 * ============================================================================================================
 * THE THREE REFUSALS ARE SENTENCES, NOT STATUS CODES
 * ============================================================================================================
 * Each is knowable before the request and is rendered before it: a prescriber should not learn from a 409
 * that they asked a patient to return medicine.
 */

const REASONS = [
  { code: "ClinicalChange", nameEn: "Clinical change", nameAr: "تغير إكلينيكي" },
  { code: "Duplicate", nameEn: "Duplicate", nameAr: "مكرر" },
];

function open(over: Partial<Parameters<typeof AmendLineDialog>[0]> = {}) {
  const onConfirm = vi.fn().mockResolvedValue(undefined);
  const onPreview = vi.fn().mockResolvedValue({
    outcome: "Reallocated", newTotal: 180, alreadyDispensed: 90,
    remainingWindows: [90], unit: "PrescribingUnits", missingField: null,
  });
  renderNode(
    <AmendLineDialog
      open
      action="amend-schedule"
      lineLabel="Amlodipine 5mg"
      currentDurationDays={90}
      currentFrequencyMonths={1}
      reasons={REASONS}
      onPreview={onPreview}
      onCancel={() => {}}
      onConfirm={onConfirm}
      {...over}
    />,
  );
  return { onConfirm, onPreview };
}

describe("Chronic schedule amendment (32.6)", () => {
  it("shows the recomputed schedule with the collected portion marked immutable", async () => {
    const user = userEvent.setup();
    open();

    await user.clear(screen.getByLabelText(/duration/i));
    await user.type(screen.getByLabelText(/duration/i), "60");

    const preview = await screen.findByTestId("chronic-preview");
    // The doctor's question is "what happens to what has already been collected?", so that answer leads.
    // The collected quantity, inside the preview section and marked as what it is.
    expect(within(preview).getByText(/collected/i)).toBeInTheDocument();
    expect(within(preview).getByText(/immutable|cannot change/i)).toBeInTheDocument();
  });

  it("refuses a total below what has already been collected, in words", async () => {
    const user = userEvent.setup();
    const { onConfirm } = open({
      onPreview: vi.fn().mockResolvedValue({
        outcome: "BelowDispensed", newTotal: 90, alreadyDispensed: 180,
        remainingWindows: [], unit: "PrescribingUnits", missingField: null,
      }),
    });

    await user.clear(screen.getByLabelText(/duration/i));
    await user.type(screen.getByLabelText(/duration/i), "30");

    expect(await screen.findByText(/already been collected/i)).toBeInTheDocument();
    // Not merely a message beside an enabled button: un-dispensing is not a thing that can happen.
    expect(screen.getByRole("button", { name: /replace with new version/i })).toBeDisabled();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it("asks before turning a chronic script acute, and will not proceed until told", async () => {
    const user = userEvent.setup();
    open({
      onPreview: vi.fn().mockResolvedValue({
        outcome: "NoLongerChronic", newTotal: 60, alreadyDispensed: 0,
        remainingWindows: [], unit: "PrescribingUnits", missingField: null,
      }),
    });

    await user.clear(screen.getByLabelText(/duration/i));
    await user.type(screen.getByLabelText(/duration/i), "20");

    // Worded as what it does to the patient's expectation, not as a flag: the dispensing pattern they were
    // told to expect is what changes.
    expect(await screen.findByText(/no longer a chronic prescription/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /replace with new version/i })).toBeDisabled();

    await user.click(screen.getByRole("checkbox", { name: /convert it to acute/i }));
    expect(screen.getByRole("button", { name: /replace with new version/i })).toBeEnabled();
  });

  it("sends duration, frequency and the conversion flag with a coded reason", async () => {
    const user = userEvent.setup();
    const { onConfirm } = open();

    await user.clear(screen.getByLabelText(/duration/i));
    await user.type(screen.getByLabelText(/duration/i), "60");
    await screen.findByTestId("chronic-preview");

    await user.click(screen.getByRole("combobox", { name: /reason/i }));
    await user.click(await screen.findByRole("option", { name: /clinical change/i }));
    await user.click(screen.getByRole("button", { name: /replace with new version/i }));

    await waitFor(() => expect(onConfirm).toHaveBeenCalledWith(expect.objectContaining({
      durationDays: 60, frequencyMonths: 1, reasonCode: "ClinicalChange", convertToAcute: false,
    })));
  });

  it("says the arithmetic could not run rather than showing a schedule", async () => {
    const user = userEvent.setup();
    open({
      onPreview: vi.fn().mockResolvedValue({
        outcome: "NotChecked", newTotal: 0, alreadyDispensed: 90,
        remainingWindows: [], unit: "PrescribingUnits", missingField: "is_pack_splittable",
      }),
    });

    await user.clear(screen.getByLabelText(/duration/i));
    await user.type(screen.getByLabelText(/duration/i), "60");

    // NotChecked is not zero. An amendment must not be the route by which a missing is_pack_splittable
    // quietly becomes an assumed one.
    expect(await screen.findByText(/is_pack_splittable/)).toBeInTheDocument();
    expect(screen.queryByTestId("chronic-preview")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /replace with new version/i })).toBeDisabled();
  });
});
