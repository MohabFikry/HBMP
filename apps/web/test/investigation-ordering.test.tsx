import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { InvestigationWorkspace } from "../src/screens/investigations/InvestigationWorkspace";

/**
 * Ordering labs and imaging (the investigation workspace).
 *
 * <p>What this replaced was a modal with two text inputs pre-filled with a hard-coded LOINC code and the
 * words "Complete blood count" — one line, no catalogue, no checks. So the assertions below are mostly
 * about the things that modal could not do at all: reach a real catalogue, carry more than one line, and
 * say something honest about each of them before it is sent.</p>
 */

function renderWorkspace(
  orderType: "Lab" | "Imaging" = "Lab",
  api: ApiClient = new DevApiClient({ latencyMs: 0 }),
  diagnoses: string[] = ["E11.9"],
) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <InvestigationWorkspace encounterId="enc-77" beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231" orderType={orderType} diagnosisIcdCodes={diagnoses} />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** Type into the combobox and pick the first result. */
async function chooseTest(user: ReturnType<typeof userEvent.setup>, label: string, query: string, index = 0) {
  const boxes = screen.getAllByRole("combobox", { name: label });
  await user.type(boxes[index], query);
  const options = await screen.findAllByRole("option");
  await user.click(options[0]);
}

describe("the catalogue is real, and scoped to the section", () => {
  it("searches CPT by name and keeps the code visible after choosing", async () => {
    const user = userEvent.setup();
    renderWorkspace("Lab");

    // Lower case against a description that reads "(CBC)" — the search is case-insensitive on both fields,
    // and "blood" no longer names one row on its own now that the fixture carries a glucose test too.
    await chooseTest(user, "Test", "cbc");

    // The code is what travels to the lab, appears on the worklist and is quoted in a claim. A screen that
    // shows only the description after selection hides the one string everyone downstream works from.
    expect(await screen.findByText(/CPT 85025/)).toBeInTheDocument();
  });

  it("asks for the sections its tab orders from — and Labs is two of them", async () => {
    const user = userEvent.setup();
    const searchCpt = vi.fn().mockResolvedValue([]);
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { searchCpt: unknown }).searchCpt = searchCpt;

    renderWorkspace("Imaging", api);
    await user.type(screen.getByRole("combobox", { name: "Study" }), "chest");
    // `expect.anything()` for the third argument: the combobox now passes an AbortSignal so a superseded
    // keystroke's request is cancelled rather than merely ignored. What this test is about is the SECTIONS.
    await waitFor(() => expect(searchCpt).toHaveBeenCalledWith("chest", ["Imaging"], expect.anything()));

    searchCpt.mockClear();
    renderWorkspace("Lab", api);
    await user.type(screen.getAllByRole("combobox", { name: "Test" })[0], "chest");
    // TWO sections, not one. A sample run on an analyser and a specimen read by a pathologist are ordered
    // from the same tab and are not the same section — asking only for Laboratory would silently drop
    // every 88xxx code (surgical pathology, cytopathology) out of the doctor's reach.
    await waitFor(() =>
      expect(searchCpt).toHaveBeenCalledWith("chest", ["Laboratory", "Pathology"], expect.anything()));
  });

  it("reaches pathology from the Labs tab and refuses imaging there", async () => {
    const user = userEvent.setup();
    // The real fixture client, not a spy: this asserts what the doctor is OFFERED, which is the sections
    // and the search agreeing, not merely the arguments one passes the other.
    const labs = renderWorkspace("Lab");
    await user.type(screen.getAllByRole("combobox", { name: "Test" })[0], "patholog");
    expect(await screen.findByText(/CPT 88305/)).toBeInTheDocument();

    // Unmounted before the second render, or the Labs result stays in the document and the negative below
    // passes against the screen it was meant to rule out.
    labs.unmount();
    renderWorkspace("Imaging");
    await user.type(screen.getByRole("combobox", { name: "Study" }), "patholog");
    // Ordering a scan from the Labs tab — or a biopsy report from Imaging — is not something a doctor has to
    // notice and avoid. It is not offered. (The server still refuses it on submit; a filtered list is a
    // convenience, not a rule.)
    await waitFor(() => expect(screen.queryByText(/CPT 88305/)).not.toBeInTheDocument());
  });

  it("leads with the code when a digit is typed and with the description when a word is", async () => {
    const user = userEvent.setup();
    renderWorkspace("Lab");
    const box = screen.getAllByRole("combobox", { name: "Test" })[0];

    // "82947" is the glucose code AND appears in the basic metabolic panel's description, because CPT panel
    // descriptions cite their component codes. Sorted by code, the panel (80048) comes first and the code
    // the doctor typed is the second row. That is the ordering this asserts against.
    await user.type(box, "82947");
    let options = await screen.findAllByRole("option");
    expect(options).toHaveLength(2);
    expect(options[0]).toHaveTextContent("CPT 82947");

    // A worded query ranks on the text — which is every match here, since no CPT code begins with a letter.
    // Pinned so a later change to match codes by containment cannot quietly reorder a worded search.
    await user.clear(box);
    await user.type(box, "panel");
    options = await screen.findAllByRole("option");
    expect(options[0]).toHaveTextContent(/CPT 800(48|53)/);
  });
});

describe("more than one line", () => {
  it("adds and removes lines, and one order carries them all", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const submit = vi.spyOn(api, "submitInvestigationOrder");
    renderWorkspace("Lab", api as unknown as ApiClient);

    await chooseTest(user, "Test", "cbc");
    await user.click(screen.getByRole("button", { name: /add another/i }));
    await chooseTest(user, "Test", "metabolic", 0);

    await user.click(screen.getByRole("button", { name: "Check" }));
    await user.click(await screen.findByRole("button", { name: /send order/i }));

    // ONE order with two lines, not two orders. A panel of tests ordered together is one clinical request,
    // and splitting it would give the patient two reference numbers for one visit to the lab.
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    expect(submit.mock.calls[0][0].lines).toHaveLength(2);
  });
});

describe("the same sequence as prescribing", () => {
  it("will not send before the lines have been checked", async () => {
    const user = userEvent.setup();
    renderWorkspace("Lab");

    await chooseTest(user, "Test", "cbc");
    // Composed and valid-looking, but nothing has been asked about it yet. Send stays shut until it has.
    expect(screen.getByRole("button", { name: /send order/i })).toBeDisabled();

    await user.click(screen.getByRole("button", { name: "Check" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /send order/i })).toBeEnabled());
  });

  it("reopens the gate when a line changes after the check", async () => {
    const user = userEvent.setup();
    renderWorkspace("Lab");

    await chooseTest(user, "Test", "cbc");
    await user.click(screen.getByRole("button", { name: "Check" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /send order/i })).toBeEnabled());

    // A verdict belongs to the lines it was given for. Editing one and sending on the old verdict is how a
    // screen reports a check it never ran.
    await user.clear(screen.getByLabelText("Quantity"));
    await user.type(screen.getByLabelText("Quantity"), "3");

    expect(await screen.findByText(/Check again before sending/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /send order/i })).toBeDisabled();
  });

  it("says an unanswered check is unanswered rather than showing it as fine", async () => {
    const user = userEvent.setup();
    renderWorkspace("Lab");

    await chooseTest(user, "Test", "cbc");
    await user.click(screen.getByRole("button", { name: "Check" }));

    // There is no procedure-indication reference in this platform. The panel says so, in the same
    // dashed-and-hollow treatment prescribing uses, instead of a green tick that would read as approval.
    await user.click(await screen.findByRole("button", { name: /Checks for/i }));
    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByText(/No procedure-indication reference is loaded/i)).toBeInTheDocument();
  });

  it("reports that there is nothing to check against when no diagnosis is recorded", async () => {
    renderWorkspace("Lab", new DevApiClient({ latencyMs: 0 }), []);
    // Same rule as prescribing: an empty diagnosis list is a fact about the encounter that is stated,
    // not a condition that silently makes a check pass.
    expect(await screen.findByText(/No diagnosis is recorded on this encounter/i)).toBeInTheDocument();
  });
});

describe("accessibility", () => {
  it("has no serious or critical violations", async () => {
    const { container } = renderWorkspace("Lab");
    await screen.findByRole("combobox", { name: "Test" });
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

/**
 * The composed-but-unsent order, across a reload.
 *
 * Same reasoning as the prescribing workspace: compose → check → acknowledge → send lived entirely in
 * component state, so an accidental F5 emptied the composer and discarded a check that had already run and
 * warnings that had already been answered in writing. A remount stands in for the reload — it re-runs the
 * lazy initializer that reads the store, which is the path a refreshed page takes.
 */
describe("a reload does not throw the work away", () => {
  it("keeps the lines and the check after a remount", async () => {
    const user = userEvent.setup();
    const first = renderWorkspace("Lab");

    await chooseTest(user, "Test", "cbc");
    await user.click(screen.getByRole("button", { name: /^check/i }));
    await waitFor(() => expect(
      screen.getAllByRole("button").some((b) => b.className.includes("rx-status--button")),
    ).toBe(true));

    first.unmount();
    renderWorkspace("Lab");

    expect(await screen.findByText(/CPT 85025/)).toBeInTheDocument();
    // Restoring the lines but not the check would silently demote every line to NotChecked and send the
    // doctor round the loop again — the same loss, one step later.
    await waitFor(() => expect(
      screen.getAllByRole("button").some((b) => b.className.includes("rx-status--button")),
    ).toBe(true));
  });

  it("keeps the Labs draft out of the Imaging tab", async () => {
    const user = userEvent.setup();
    const labs = renderWorkspace("Lab");
    await chooseTest(user, "Test", "cbc");
    expect(await screen.findByText(/CPT 85025/)).toBeInTheDocument();

    labs.unmount();
    renderWorkspace("Imaging");

    // The two tabs are the same component with different sections. Keyed by encounter alone, a half-composed
    // lab order would restore into the imaging composer — carrying a CPT code that tab cannot even offer.
    await waitFor(() => expect(screen.queryByText(/CPT 85025/)).not.toBeInTheDocument());
  });
});
