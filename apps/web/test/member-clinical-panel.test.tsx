import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { MemberClinicalPanel } from "../src/screens/encounter/MemberClinicalPanel";

/**
 * Allergies + blood group on the member's file (recorded from the encounter).
 *
 * The assertions below are mostly about ABSENCE, because absence is where this panel can lie. "No allergies
 * recorded" and "no allergies" are different clinical claims; so are "not recorded" and "we could not load
 * it". Each has its own rendering and each is pinned here — the same discipline the prescribing workspace
 * applies to its five check states.
 */

function renderPanel(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <MemberClinicalPanel beneficiaryId="ben-1" />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** Pick an option from one of the dialog's select-only comboboxes, by accessible name. */
async function choose(user: ReturnType<typeof userEvent.setup>, name: RegExp, option: RegExp) {
  const dialog = await screen.findByRole("dialog");
  await user.click(within(dialog).getByRole("combobox", { name }));
  await user.click(await screen.findByRole("option", { name: option }));
}

describe("recorded-versus-not-recorded", () => {
  it("says NO ALLERGIES RECORDED rather than showing a calm blank", async () => {
    renderPanel();
    // The wording matters as much as the presence: it has to deny the inference a reader would otherwise
    // draw from an empty list, which is that the patient has been screened and is clear.
    const empty = await screen.findByText(/no allergies recorded/i);
    expect(empty.textContent).toMatch(/not the same as none/i);
  });

  it("renders an unrecorded blood group as an explicit state, not as a missing field", async () => {
    renderPanel();
    const blood = await screen.findByRole("button", { name: /blood group/i });
    expect(blood.textContent).toMatch(/not recorded/i);
    // The dashed treatment is a real cue and not decoration — assert the hook the stylesheet keys on.
    expect(blood.getAttribute("data-recorded")).toBe("no");
  });

  it("does NOT report an empty record when the read failed", async () => {
    renderPanel(new DevApiClient({ latencyMs: 0, fault: "error" }));
    await screen.findByText(/could not be loaded/i);
    // The single most important negative in this file: an outage must never produce the empty-state
    // sentence, because that sentence is a clinical claim about the patient.
    expect(screen.queryByText(/no allergies recorded/i)).toBeNull();
  });
});

describe("recording", () => {
  it("records an allergy and shows it on the panel", async () => {
    const user = userEvent.setup();
    renderPanel();
    await screen.findByText(/no allergies recorded/i);

    await user.click(screen.getByRole("button", { name: /add allergy/i }));
    await choose(user, /allergen/i, /penicillins/i);
    await choose(user, /severity/i, /severe/i);
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    // Named, with its severity, and the empty state gone — the write reached the list, not just the toast.
    const chip = await screen.findByText("Penicillins");
    expect(chip.closest("li")?.getAttribute("data-severity")).toBe("Severe");
    await waitFor(() => expect(screen.queryByText(/no allergies recorded/i)).toBeNull());
  });

  it("refuses to record an allergy with no allergen chosen", async () => {
    const user = userEvent.setup();
    renderPanel();
    await screen.findByText(/no allergies recorded/i);

    await user.click(screen.getByRole("button", { name: /add allergy/i }));
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    await screen.findByText(/choose an allergen/i);
    // Still open, so the reason is visible beside the field it is about.
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("records a blood group and shows it in place of the unrecorded state", async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.click(await screen.findByRole("button", { name: /blood group/i }));
    await choose(user, /blood group/i, /^O\+$/);
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      const blood = screen.getByRole("button", { name: /blood group/i });
      expect(blood.getAttribute("data-recorded")).toBe("yes");
      expect(blood.textContent).toContain("O+");
    });
  });
});

describe("accessibility", () => {
  it("has no axe violations in English or Arabic", async () => {
    const { container, unmount } = renderPanel();
    await screen.findByText(/no allergies recorded/i);
    expect(await axe(container)).toHaveNoViolations();
    unmount();

    document.documentElement.setAttribute("dir", "rtl");
    document.documentElement.setAttribute("lang", "ar");
    const ar = renderPanel();
    await screen.findByText(/no allergies recorded/i);
    expect(await axe(ar.container)).toHaveNoViolations();
    document.documentElement.setAttribute("dir", "ltr");
    document.documentElement.setAttribute("lang", "en");
  });
});
