import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { DrugCombobox } from "../src/screens/prescribing/DrugCombobox";

/**
 * 29.7 / design 45 §7 — the lowest-price chip and the availability tri-state.
 *
 * <p>The assertion that matters most is the negative one: <b>Unknown renders NOTHING</b>. All 31,651 drugs
 * default to Unknown, so an indicator that fired on Unknown would fire on every row — and prescribers would
 * learn to ignore it before it ever carried real data.</p>
 */
function renderCombobox() {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <DrugCombobox value={null} onChange={() => {}} />
    </AppProviders>,
  );
}

async function search(term: string) {
  const user = userEvent.setup();
  renderCombobox();
  await user.type(screen.getByRole("combobox"), term);
  return user;
}

describe("29.7 — lowest price and availability", () => {
  it("shows a Lowest price chip on the cheapest per-unit option", async () => {
    await search("amox");

    expect(await screen.findByText(/lowest price/i, {}, { timeout: 3000 })).toBeInTheDocument();
  });

  it("renders NOTHING for a drug whose availability is Unknown", async () => {
    // Searched by a term that matches ONLY the Unknown fixture. Searching "amox" would also return the
    // deliberately-Unavailable product, and its badge would satisfy a naive negative assertion by accident.
    const { container } = renderCombobox();
    const user = userEvent.setup();
    await user.type(screen.getByRole("combobox"), "amoxicare");
    await screen.findByRole("listbox", {}, { timeout: 3000 });

    // Not a badge, not a neutral chip, not a warning. Unknown is the DEFAULT for the whole catalogue.
    expect(container.textContent).not.toMatch(/unknown/i);
    expect(screen.queryByText(/unavailable/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/out of stock/i)).not.toBeInTheDocument();
  });

  it("shows a badge only for a positive Unavailable", async () => {
    await search("stockout");

    expect(await screen.findByText(/^unavailable$/i, {}, { timeout: 3000 })).toBeInTheDocument();
  });

  it("does not label a drug whose pack size is unknown, however cheap its pack looks", async () => {
    // The 29.7 correction, at the UI: a drug with no pack size has no per-unit price, and falling back to
    // pack price is exactly the comparison that would point a prescriber at the dearer box.
    await search("nopack");
    await screen.findByRole("listbox", {}, { timeout: 3000 });

    expect(screen.queryByText(/lowest price/i)).not.toBeInTheDocument();
  });
});
