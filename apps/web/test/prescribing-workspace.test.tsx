import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { PrescribingWorkspace } from "../src/screens/prescribing/PrescribingWorkspace";
import { clearTokens } from "../src/auth/tokenStore";

/**
 * The prescribing workspace (phase 26.5, design 43 §6).
 *
 * Two invariants dominate: a clinical check may warn but never block, and "check unavailable" must never
 * render as "OK". The second is asserted against the RENDERED DOM rather than against the data, because the
 * failure it guards against is visual — a hurried reader scanning a column of status chips.
 */

function renderWorkspace(
  diagnoses: string[] = ["E11.9"],
  api: ApiClient = new DevApiClient({ latencyMs: 0 }),
) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PrescribingWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" diagnosisIcdCodes={diagnoses} />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** Open the per-line checks dialog from the status chip — the findings live behind it now. */
async function openChecks(user: ReturnType<typeof userEvent.setup>) {
  const chip = screen.getAllByRole("button").find((b) => b.className.includes("rx-status--button"));
  if (!chip) throw new Error("no status chip button — was a validation run produced?");
  await user.click(chip);
  return screen.findByRole("dialog");
}

/** Type into the combobox and pick the first option. */
async function pickDrug(user: ReturnType<typeof userEvent.setup>, query: string, nth = 0) {
  const boxes = screen.getAllByRole("combobox");
  await user.type(boxes[nth], query);
  const options = await screen.findAllByRole("option");
  await user.click(options[0]);
}

describe("drug combobox", () => {
  it("finds the same product by trade name AND by active ingredient", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await user.type(screen.getByRole("combobox"), "augmentin");
    const byTrade = await screen.findAllByRole("option");
    expect(byTrade.map((o) => o.textContent).join(" ")).toContain("Augmentin");

    await user.clear(screen.getByRole("combobox"));
    await user.type(screen.getByRole("combobox"), "clavulanic");
    const byIngredient = await screen.findAllByRole("option");
    expect(byIngredient.map((o) => o.textContent).join(" ")).toContain("Augmentin");
  });

  it("shows the active ingredient and price BEFORE the drug is chosen", async () => {
    // A safety feature, not decoration: two trade names holding the same molecule is the commonest
    // prescribing duplication, and the molecule has to be visible at the moment of choosing.
    const user = userEvent.setup();
    renderWorkspace();

    await user.type(screen.getByRole("combobox"), "augmentin");
    const option = (await screen.findAllByRole("option"))[0];

    expect(option.textContent).toContain("amoxicillin + clavulanic acid");
    expect(option.textContent).toContain("210");
  });

  it("is a real ARIA combobox and is operable by keyboard alone", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    const box = screen.getByRole("combobox");
    expect(box).toHaveAttribute("aria-autocomplete", "list");
    expect(box).toHaveAttribute("aria-controls");

    await user.type(box, "amox");
    await screen.findAllByRole("option");
    expect(box).toHaveAttribute("aria-expanded", "true");

    // Arrow to the second option, then Enter — no mouse anywhere.
    await user.keyboard("{ArrowDown}");
    const active = box.getAttribute("aria-activedescendant");
    expect(active).toBeTruthy();
    await user.keyboard("{Enter}");

    // Chosen, and the ingredient is STILL visible — the cue must not disappear at review time.
    await waitFor(() => expect(screen.queryByRole("listbox")).toBeNull());
    expect(screen.getByText(/amoxicillin/i)).toBeTruthy();
  });

  it("does not search on a single character", async () => {
    const search = vi.fn().mockResolvedValue([]);
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { searchPrescribableDrugs: unknown }).searchPrescribableDrugs = search;
    const user = userEvent.setup();
    renderWorkspace(["E11.9"], api);

    await user.type(screen.getByRole("combobox"), "a");
    await new Promise((r) => setTimeout(r, 400));

    expect(search).not.toHaveBeenCalled();
  });
});

describe("five per-line states", () => {
  it("renders 'Check unavailable' as VISUALLY DISTINCT from OK — never as a tick", async () => {
    // THE test for design 43 invariant 2, asserted on the DOM. The fixture's fourth product has an allergy
    // source that is down, so its line must summarise as Unavailable.
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "vero");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

    const unavailable = await screen.findAllByText(/Check unavailable/i);
    expect(unavailable.length).toBeGreaterThan(0);

    const chip = unavailable[0].closest(".rx-status") as HTMLElement;
    expect(chip.dataset.state).toBe("Unavailable");

    // It is in the UNANSWERED visual class — a different kind of thing from OK, not a paler shade of it.
    expect(chip.className).toContain("rx-status--unanswered");
    expect(chip.className).not.toContain("rx-status--answered");
    expect(chip.className).not.toContain("rx-status--ok");
  });

  it("distinguishes 'not checked' from 'OK' for a drug with no indication data", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "vero");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await openChecks(user);

    expect(await screen.findByText(/no indication data is recorded/i)).toBeTruthy();
  });

  it("reports 'no diagnosis recorded' rather than passing when the encounter has none", async () => {
    const user = userEvent.setup();
    renderWorkspace([]);

    expect(screen.getByText(/no diagnosis is recorded/i)).toBeTruthy();

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await openChecks(user);

    expect(await screen.findByText(/no diagnosis recorded/i)).toBeTruthy();
  });

  it("carries the source, version and caveat on every finding that had one", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await openChecks(user);

    // Collapsed, not removed. Doc 43 §1 rule 2 requires the prescriber be able to attribute an advisory;
    // it is one disclosure away rather than printed under every finding.
    await user.click(screen.getByText(/^Sources$|^المصادر$/));

    expect(await screen.findByText(/Drug indication list/i)).toBeTruthy();
    // The source's own statement of its limits — what tells a prescriber how much weight to give it.
    expect(screen.getAllByText(/not from a published dataset/i).length).toBeGreaterThan(0);
  });
});

describe("validate → submit gating", () => {
  it("cannot submit before a validation run", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "augmentin");

    const submit = screen.getByRole("button", { name: /^submit|إرسال/i });
    expect(submit).toBeDisabled();
  });

  it("invalidates the run when a line is edited afterwards", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "glucophage");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeEnabled());

    // Change the quantity — the previous verdict no longer describes what is on screen.
    const qty = screen.getByRole("spinbutton", { name: /quantity|الكمية/i });
    await user.clear(qty);
    await user.type(qty, "5");

    expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeDisabled();
    expect(screen.getByText(/validate again/i)).toBeTruthy();
  });

  it("blocks submit until a warning is acknowledged WITH a reason, then allows it", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    // Glucophage against E11.9 matches; Augmentin does not, so it warns (off-label).
    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

    expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeDisabled();
    expect(screen.getByText(/Every warning needs a reason/i)).toBeTruthy();

    await openChecks(user);
    await screen.findByText(/Not a listed indication/i);

    const reason = screen.getByPlaceholderText(/why proceed|لماذا المتابعة/i);
    await user.type(reason, "Treating a confirmed sinus infection");

    // Close the dialog before looking at Submit: Radix marks the rest of the page aria-hidden while it is
    // open, which is correct — and means the button genuinely is not reachable until the reason is given
    // and the prescriber comes back out.
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    await waitFor(() => expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeEnabled());
  });

  it("an off-label indication WARNS and never blocks", async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

    const warning = await screen.findAllByText(/Warning|تحذير/i);
    expect(warning.length).toBeGreaterThan(0);
    // Off-label prescribing is legitimate and common — it is overridable, so no "Blocked" anywhere.
    expect(screen.queryByText(/^Blocked$/)).toBeNull();
  });
});

/*
 * ================================================================= severity tiering (28.4, doc 44 §2)
 *
 * Severity existed on the wire since phase 26 and was NEVER READ by the UI — it was interpolated into the
 * message string, where it read as prose rather than as a cue. So a contraindicated interaction and a minor
 * one rendered identically and demanded the same click, which is the documented mechanism behind override
 * rates above 90%: when everything looks the same, everything gets dismissed.
 *
 * Asserted against the rendered DOM rather than the data, because the failure being guarded against is
 * visual — a hurried reader scanning a column of chips.
 */
describe("severity is a first-class cue", () => {
  /** A validation result carrying one finding at the given severity. */
  function resultWith(severity: string | null, requiresAcknowledgement: boolean) {
    return (lineId: string) => ({
      validationId: "11111111-1111-1111-1111-111111111111",
      ranAt: new Date().toISOString(),
      engineVersion: "28.2",
      overallState: "Warning" as const,
      findings: [{
        lineId,
        drugId: null,
        kind: "Interaction" as const,
        state: "Warning" as const,
        messageEn: "Interaction with another medicine on this prescription.",
        messageAr: "تداخل دوائي مع دواء آخر في هذه الوصفة.",
        sourceName: "Mersal interaction list",
        sourceVersion: "seed-v1",
        checkedAt: new Date().toISOString(),
        caveat: null,
        referenceText: null,
        severity,
        relatedLineId: null,
        requiresAcknowledgement,
        requiresTypedReason: severity === "Contraindicated",
        isBlocking: false,
      }],
      lineStates: { [lineId]: "Warning" as const },
    });
  }

  function apiReturning(severity: string | null, requiresAcknowledgement: boolean): ApiClient {
    const api = new DevApiClient({ latencyMs: 0 });
    const build = resultWith(severity, requiresAcknowledgement);
    return new Proxy(api, {
      get(target, prop, receiver) {
        if (prop === "validatePrescription") {
          return async (req: { lines: { lineId: string }[] }) => build(req.lines[0].lineId);
        }
        return Reflect.get(target, prop, receiver) as unknown;
      },
    }) as ApiClient;
  }

  it("SHOWS THE SEVERITY ON THE LINE, without the modal being opened", async () => {
    // The acceptance criterion doc 44 §2 states in as many words. A prescriber must be able to tell a
    // contraindicated line from a minor one while scanning, not one click away.
    const user = userEvent.setup();
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={apiReturning("Contraindicated", true)}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <PrescribingWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" diagnosisIcdCodes={["E11.9"]} />
        </MemoryRouter>
      </AppProviders>,
    );

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

    const chip = await screen.findByText(/^Contraindicated$/);
    expect(chip).toBeTruthy();
    // The chip carries the tier as DATA as well as text, so the styling cue and the accessible name cannot
    // drift apart.
    expect(chip.closest("[data-severity]")?.getAttribute("data-severity")).toBe("Contraindicated");
  });

  it("A MODERATE FINDING RENDERS INLINE AND DOES NOT GATE SUBMIT", async () => {
    // The whole point of the tier. Moderate is worth seeing and is not worth stopping for, and the server
    // says so through requiresAcknowledgement — this screen does not re-derive it.
    const user = userEvent.setup();
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={apiReturning("Moderate", false)}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <PrescribingWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" diagnosisIcdCodes={["E11.9"]} />
        </MemoryRouter>
      </AppProviders>,
    );

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

    await screen.findByText(/^Moderate$/);
    expect(screen.queryByText(/Every warning needs a reason/i)).toBeNull();
    await waitFor(() => expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeEnabled());
  });

  it("an UNGRADED finding still gates, and shows no severity chip", async () => {
    // A manufacturer label states an effect rather than a rank. Treating "ungraded" as "not serious" would
    // be the UI inventing a clinical judgement it has no source for.
    const user = userEvent.setup();
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={apiReturning(null, true)}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <PrescribingWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" diagnosisIcdCodes={["E11.9"]} />
        </MemoryRouter>
      </AppProviders>,
    );

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));

    await waitFor(() => expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeDisabled());
    expect(document.querySelector("[data-severity]")).toBeNull();
  });

  it("has no axe violations with a severity chip on the line", async () => {
    const user = userEvent.setup();
    const { container } = render(
      <AppProviders authClient={new DevAuthClient()} apiClient={apiReturning("Major", true)}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <PrescribingWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" diagnosisIcdCodes={["E11.9"]} />
        </MemoryRouter>
      </AppProviders>,
    );

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await screen.findByText(/^Major$/);

    expect(await axe(container)).toHaveNoViolations();
  });
});

describe("accessibility", () => {
  it("has no axe violations against POPULATED fixtures, in English", async () => {
    // Populated on purpose: an axe run over an empty screen proves the empty state is accessible and
    // nothing else. The combobox options, the five status cues and the expanded findings are the surface
    // that needs checking, and none of them render without data.
    const user = userEvent.setup();
    renderWorkspace();

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await openChecks(user);
    await screen.findByText(/Not a listed indication/i);

    // The dialog renders in a portal, so axe the whole document rather than the render container.
    expect(await axe(document.body)).toHaveNoViolations();
  });

  it("has no axe violations with the option list open", async () => {
    const user = userEvent.setup();
    const { container } = renderWorkspace();

    await user.type(screen.getByRole("combobox"), "amox");
    await screen.findAllByRole("option");

    expect(await axe(container)).toHaveNoViolations();
  });
});

describe("the real-uuid regression", () => {
  it("submits the drug's UUID, never its ATC code", async () => {
    // The defect this workspace was built to fix: the old modal sent `drugId: req.drug.code` — the ATC
    // STRING — where the API expects a Guid, so the prescribing path could not work against real data.
    const submit = vi.fn().mockResolvedValue({ prescriptionId: "rx-1", rxNo: "RX-2026-000001", status: "Submitted" });
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { submitPrescription: unknown }).submitPrescription = submit;

    const user = userEvent.setup();
    renderWorkspace(["E11.9"], api);

    await pickDrug(user, "glucophage");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeEnabled());
    await user.click(screen.getByRole("button", { name: /^submit|إرسال/i }));

    await waitFor(() => expect(submit).toHaveBeenCalled());
    const sent = submit.mock.calls[0][0] as { lines: { drug: { drugId: string; atcCode?: string } | null }[] };
    const drugId = sent.lines[0].drug!.drugId;

    expect(drugId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
    expect(drugId).not.toBe(sent.lines[0].drug!.atcCode);
    expect(drugId).not.toMatch(/^[A-Z]\d{2}[A-Z]{2}\d{2}$/);
  });

  it("clears the composer and tells the caller to re-read after a successful submit", async () => {
    // A submitted prescription that is still sitting in the composer, still showing a green validated chip,
    // reads as one that did not save — and the reasonable next move for a doctor is to press Submit again.
    // The `onDone` half is what refreshes the list above it; without it the prescription is genuinely absent
    // from the screen until a reload, which is indistinguishable from a failed write.
    const submit = vi.fn().mockResolvedValue({ prescriptionId: "rx-1", rxNo: "RX-2026-000001", status: "Submitted" });
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { submitPrescription: unknown }).submitPrescription = submit;
    const onDone = vi.fn();

    const user = userEvent.setup();
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={api}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <PrescribingWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" diagnosisIcdCodes={["E11.9"]} onDone={onDone} />
        </MemoryRouter>
      </AppProviders>,
    );

    await pickDrug(user, "glucophage");
    await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeEnabled());
    await user.click(screen.getByRole("button", { name: /^submit|إرسال/i }));

    await waitFor(() => expect(onDone).toHaveBeenCalled());
    // Back to one empty line: the chosen drug is gone, and Submit is disabled again because nothing has
    // been composed or validated.
    await waitFor(() => {
      expect(screen.getAllByRole("combobox")).toHaveLength(1);
      expect((screen.getByRole("combobox") as HTMLInputElement).value).toBe("");
      expect(screen.getByRole("button", { name: /^submit|إرسال/i })).toBeDisabled();
    });
  });
});

describe("Arabic", () => {
  it("renders the workspace and its status words in Arabic without axe violations", async () => {
    localStorage.setItem("mersal-lang", "ar");
    try {
      const user = userEvent.setup();
      renderWorkspace();

      await pickDrug(user, "augmentin");
      await user.click(screen.getByRole("button", { name: /validate|تحقّق/i }));
      await openChecks(user);

      // The Arabic message, not a fallback to English.
      await waitFor(() =>
        expect(screen.getByText(/ليس من دواعي الاستعمال المسجلة/)).toBeTruthy(),
      );
      expect(await axe(document.body)).toHaveNoViolations();
    } finally {
      localStorage.removeItem("mersal-lang");
    }
  });
});

/**
 * The composed-but-unsent draft, across a reload.
 *
 * ============================================================================================================
 * WHY THIS IS NOT A CONVENIENCE
 * ============================================================================================================
 * Prescribing is compose → validate → acknowledge → send, and all four steps lived in component state. A
 * refresh — an accidental F5, a browser restore, a laptop lid — emptied the composer back to one blank line.
 * That is not just retyping a drug name: the check had already run and its warnings had been ANSWERED IN
 * WRITING. A doctor made to justify an off-label choice a second time, from memory, is a doctor whose reason
 * shortens to "as discussed", and that sentence is the audit record.
 *
 * A remount with the same key stands in for the reload: it re-runs the lazy initializer that reads the store,
 * which is precisely the restore path a refreshed page takes.
 */
describe("a reload does not throw the work away", () => {
  it("keeps the lines AND the validation result after a remount", async () => {
    const user = userEvent.setup();
    const first = renderWorkspace();

    await pickDrug(user, "augmentin");
    await user.click(screen.getByRole("button", { name: /validate/i }));
    // The chip only becomes a button once there is a result behind it to open.
    await waitFor(() => expect(
      screen.getAllByRole("button").some((b) => b.className.includes("rx-status--button")),
    ).toBe(true));

    first.unmount();
    renderWorkspace();

    // The drug survived...
    expect(await screen.findByText(/Augmentin/)).toBeInTheDocument();
    // ...and so did the CHECK. Restoring the lines but not the result would silently demote every line to
    // NotChecked and send the doctor round the loop again — the same loss, one step later.
    await waitFor(() => expect(
      screen.getAllByRole("button").some((b) => b.className.includes("rx-status--button")),
    ).toBe(true));
    const dialog = await openChecks(user);
    expect(dialog).toBeInTheDocument();
  });

  it("does not restore a prescription that was already sent", async () => {
    const submit = vi.fn().mockResolvedValue({ prescriptionId: "rx-1", rxNo: "RX-2026-000001", status: "Submitted" });
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { submitPrescription: unknown }).submitPrescription = submit;

    const user = userEvent.setup();
    // Glucophage against a recorded E11.9 raises no warning, so Submit opens straight off the validate.
    const first = renderWorkspace(["E11.9"], api);
    await pickDrug(user, "glucophage");
    await user.click(screen.getByRole("button", { name: /validate/i }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^submit/i })).toBeEnabled());
    await user.click(screen.getByRole("button", { name: /^submit/i }));
    await waitFor(() => expect(submit).toHaveBeenCalled());

    first.unmount();
    renderWorkspace(["E11.9"], api);

    // A written prescription is not a draft. Leaving one behind would let a reload restore an unsent-looking
    // copy of something already recorded — and the obvious next move on seeing it is to press Submit again.
    expect(await screen.findByRole("combobox")).toHaveValue("");
    expect(screen.queryByText(/Glucophage/)).not.toBeInTheDocument();
    expect(sessionStorage.getItem("mrs.draft.rx:enc-77")).toBeNull();
  });

  it("discards a stored draft it cannot recognise instead of rendering it", async () => {
    // Written by an older bundle, or edited by hand. Either way it is untrusted input, and a composer
    // half-populated from a shape nobody recognises is worse than an empty one: it LOOKS composed.
    sessionStorage.setItem("mrs.draft.rx:enc-77", JSON.stringify({ lines: [{ nonsense: true }] }));
    renderWorkspace();

    expect(await screen.findByRole("combobox")).toBeInTheDocument();
    expect(screen.queryByText(/Augmentin/)).not.toBeInTheDocument();
    // And removed, rather than left to fail the same way on every reload.
    expect(sessionStorage.getItem("mrs.draft.rx:enc-77")).toBeNull();
  });

  it("drops every draft when the session ends", async () => {
    const user = userEvent.setup();
    const first = renderWorkspace();
    await pickDrug(user, "augmentin");
    await waitFor(() => expect(sessionStorage.getItem("mrs.draft.rx:enc-77")).not.toBeNull());

    // A half-composed prescription is clinical content in a browser store on a machine a clinic shares. The
    // end of a session is exactly when it stops being this user's, and the next person at that workstation
    // must not be able to reload into it.
    clearTokens();
    expect(sessionStorage.getItem("mrs.draft.rx:enc-77")).toBeNull();

    first.unmount();
    renderWorkspace();
    await waitFor(() => expect(screen.queryByText(/Augmentin/)).not.toBeInTheDocument());
  });
});
