import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ServiceHistoryModal } from "../src/screens/ServiceHistoryModal";

/**
 * 29.4 / design 45 §4 — the ONE service-history modal.
 *
 * <p>Three states have to be distinguishable in WORDS, not merely in state: has-history,
 * no-previous-occurrences, and could-not-load. The third is the one that matters — "'Could not load' must
 * never render as 'none'. A clinician reading 'no previous tests' when the service was simply unreachable
 * will re-order unnecessarily or miss a trend."</p>
 */
function renderModal(code: string) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <ServiceHistoryModal beneficiaryId="ben-1" code={code} onClose={() => {}} />
    </AppProviders>,
  );
}

describe("29.4 — service history modal", () => {
  it("shows previous occurrences and a trend when results are numeric", async () => {
    renderModal("85025");

    // Two occurrences of the SAME service — which is the whole point of the feature.
    expect(await screen.findAllByText(/complete blood count/i, {}, { timeout: 3000 })).toHaveLength(2);
    // The trend is the clinical point of the feature.
    expect(screen.getByText(/^trend$/i)).toBeInTheDocument();
    // …and the data table stays in the DOM alongside the chart (design 12 §7): the numbers are the record.
    expect(screen.getByText("11.2")).toBeInTheDocument();
    expect(screen.getByText("12.8")).toBeInTheDocument();
  });

  it("renders a restricted result as existence-only with no value anywhere", async () => {
    renderModal("80048");
    const container = document.body;

    // Existence survives — the clinician learns the test happened, and when.
    expect(await screen.findByText(/basic metabolic panel/i, {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText(/restricted/i)).toBeInTheDocument();

    // A history modal that revealed what the results inbox withholds would defeat the whole gate. There is no
    // value in the DOM because the server never sent one.
    expect(container.textContent).not.toMatch(/\d+\.\d+/);
  });

  it("says 'no previous occurrences' for a real, successful empty answer", async () => {
    renderModal("99999");

    expect(await screen.findByText(/no previous occurrences/i, {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.queryByText(/could not be loaded/i)).not.toBeInTheDocument();
  });

  it("says 'could not load' — NOT 'none' — when the read fails", async () => {
    renderModal("ERR");

    // THE distinction. These two sentences must never be the same sentence.
    expect(await screen.findByText(/could not be loaded/i, {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText(/NOT a report that there is none/i)).toBeInTheDocument();
    expect(screen.queryByText(/no previous occurrences/i)).not.toBeInTheDocument();
    // And it offers a way forward rather than leaving the clinician to guess.
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });

  it("is axe clean with history shown", async () => {
    renderModal("85025");
    await screen.findAllByText(/complete blood count/i, {}, { timeout: 3000 });
    // document.body, not the render container: Modal renders through a Radix portal, so the container the
    // render call returns is empty and an axe run over it would pass by having nothing to check.
    const container = document.body;

    // Same rule set the rest of the a11y suite uses: colour-contrast is checked by the token tests against
    // composited backgrounds, which jsdom cannot compute.
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
