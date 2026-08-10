import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import { PractitionerAdmin } from "../src/screens/PractitionerAdmin";

/**
 * A destructive write must look destructive and must be asked about.
 *
 * <b>The defect.</b> Three writes fired the moment the button was pressed, from `variant="ghost"` — the
 * app's TRANSPARENT variant, the one every "Cancel" in the product wears. Revoking a clinician's specialty
 * makes them unbookable for it; revoking their clinic drops them off that site's booking list; revoking a
 * network-tier assignment changes the rate a provider's claims price at. Two screens over, the same verb —
 * `revoke` — was already `danger` plus a confirmation modal (`AccessAdmin`), so the product carried three
 * different answers to one question.
 *
 * <b>Why the static half exists.</b> The rendering tests pin the sites that were wrong. They cannot pin the
 * FOURTH, which has not been written yet. `destructiveCallSites` walks the whole SPA for a button whose own
 * markup calls a destructive `api.*` method and fails if any is not `danger` — the check that would have
 * caught all three before they shipped, and the shape the CI gate takes.
 */

// ── The static guard ─────────────────────────────────────────────────────────────────────────────────────

const SRC = resolve(__dirname, "../src");

/** Verbs that mutate something away. `cancel*` is excluded — see the note on `destructiveCallSites`. */
const DESTRUCTIVE_API = /\bapi\.(revoke|delete|remove|terminate|void|withdraw|purge|deactivate)[A-Z]\w*/;

function tsxFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) tsxFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

interface CallSite { file: string; line: number; variant: string; call: string }

/**
 * Every `<Button>…</Button>` whose own markup calls a destructive `api.*` method.
 *
 * Deliberately narrow. It matches the call INSIDE the button element, so a button that merely opens a
 * confirmation dialog — which is what all three fixes do — is not matched at all: the call has moved to the
 * dialog's `onConfirm`. That is the point. The rule is "do not fire a destructive write straight off a
 * click", and a button that only sets state is not firing one.
 *
 * `cancel` is not in the verb list: in this domain it is overwhelmingly "cancel this dialog", and the real
 * appointment cancellations already carry `danger` plus a modal.
 */
function destructiveCallSites(): CallSite[] {
  const found: CallSite[] = [];
  for (const file of tsxFiles(SRC)) {
    const src = readFileSync(file, "utf8");
    for (const m of src.matchAll(/<Button\b([\s\S]{0,400}?)>([\s\S]{0,500}?)<\/Button>/g)) {
      const call = DESTRUCTIVE_API.exec(m[0]);
      if (!call) continue;
      found.push({
        file: file.slice(SRC.length + 1),
        line: src.slice(0, m.index).split("\n").length,
        variant: /variant=\{?["']?([a-z]+)/.exec(m[1])?.[1] ?? "secondary",
        call: call[0],
      });
    }
  }
  return found;
}

describe("no destructive write fires from a non-destructive button", () => {
  it("sees the buttons at all — otherwise every assertion here passes vacuously", () => {
    let buttons = 0;
    for (const f of tsxFiles(SRC)) buttons += (readFileSync(f, "utf8").match(/<Button\b/g) ?? []).length;
    expect(buttons).toBeGreaterThan(250);
  });

  it("marks every direct destructive call as danger", () => {
    const offenders = destructiveCallSites().filter((s) => s.variant !== "danger");
    expect(
      offenders.map((s) => `${s.file}:${s.line} ${s.call} is variant="${s.variant}"`),
      'a button calling a destructive api.* method directly must be variant="danger" — and should usually ' +
        "route through ConfirmAction instead, which moves the call out of the button entirely",
    ).toEqual([]);
  });
});

/**
 * A raw `<button>` is allowed — a combobox option, a picker row, a time slot and the shell chrome are all
 * real controls that `Button` is the wrong shape for. What is not allowed is one with NO class, which renders
 * as platform chrome. Exactly one existed: the retry control inside CallCentre's failed-load alert, which is
 * to say the single button on that screen an operator definitely has to press.
 */
describe("no button ships unstyled", () => {
  it("gives every raw <button> a class of its own", () => {
    const offenders: string[] = [];
    for (const file of tsxFiles(SRC)) {
      const src = readFileSync(file, "utf8");
      // Comments are blanked rather than deleted, so line numbers still point at the real thing. Two doc
      // comments in this codebase discuss `<button>` in prose — they are not markup and must not be reported.
      const code = src.replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, (c) => c.replace(/[^\n]/g, " "));
      for (const m of code.matchAll(/<button\b([\s\S]{0,300}?)>/g)) {
        if (/className=/.test(m[1])) continue;
        offenders.push(`${file.slice(SRC.length + 1)}:${code.slice(0, m.index).split("\n").length}`);
      }
    }
    expect(
      offenders,
      "a raw <button> with no className renders as browser chrome — use `Button`, or give it a class if it " +
        "is a genuine custom control",
    ).toEqual([]);
  });
});

// ── PractitionerAdmin ────────────────────────────────────────────────────────────────────────────────────

/** Spies on the two revokes, over the ordinary fixture so the roster and panel render as they really do. */
class RevokeSpy extends DevApiClient {
  revokedSpecialties: Array<[string, string]> = [];
  revokedBranches: Array<[string, string]> = [];

  override revokeSpecialty(id: string, code: string) {
    this.revokedSpecialties.push([id, code]);
    return super.revokeSpecialty(id, code);
  }

  override revokePractitionerBranch(id: string, branchId: string) {
    this.revokedBranches.push([id, branchId]);
    return super.revokePractitionerBranch(id, branchId);
  }
}

describe("PractitionerAdmin asks before removing a specialty or a clinic", () => {
  // role="grid", not "table": DataTable switches role when `interactive` is set.
  async function open(user: ReturnType<typeof userEvent.setup>, name: string) {
    const row = (await within(await screen.findByRole("grid")).findByText(name)).closest("tr")!;
    await user.click(row);
    return screen.getByRole("heading", { level: 2, name });
  }
  const specialties = () => within(screen.getByRole("region", { name: /^specialties$/i }));
  const clinics = () => within(screen.getByRole("region", { name: /^clinics$/i }));

  /** Youssef Adel — primary Cardiology, secondary General Practice (which is therefore removable). */
  async function openSecondarySpecialty() {
    const user = userEvent.setup();
    const api = new RevokeSpy();
    renderNode(<PractitionerAdmin />, api);
    await open(user, "Youssef Adel");
    const gp = specialties().getByText("General Practice").closest("li")!;
    return { user, api, remove: within(gp).getByRole("button", { name: /^remove$/i }) };
  }

  it("does not revoke on the click alone", async () => {
    const { user, api, remove } = await openSecondarySpecialty();
    await user.click(remove);

    // The dialog is up and NOTHING has been written.
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
    expect(api.revokedSpecialties).toEqual([]);
  });

  it("revokes once the dialog is confirmed", async () => {
    const { user, api, remove } = await openSecondarySpecialty();
    await user.click(remove);

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: /^remove$/i }));

    await waitFor(() => expect(api.revokedSpecialties).toHaveLength(1));
  });

  it("abandons the removal when the dialog is dismissed", async () => {
    const { user, api, remove } = await openSecondarySpecialty();
    await user.click(remove);

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: /cancel/i }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(api.revokedSpecialties).toEqual([]);
  });

  it("names the thing being removed — 'Remove this?' alone is not a question", async () => {
    const { user, remove } = await openSecondarySpecialty();
    await user.click(remove);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/General Practice/)).toBeInTheDocument();
  });

  it("says the removal is reversible rather than borrowing 'cannot be undone'", async () => {
    const { user, remove } = await openSecondarySpecialty();
    await user.click(remove);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/add it back afterwards/i)).toBeInTheDocument();
    // The irreversible line is reserved for actions that are. Spending it here is how it stops being read.
    expect(within(dialog).queryByText(/cannot be undone/i)).not.toBeInTheDocument();
  });

  it("puts the consequence of removing a clinic in the dialog, not in a caption beside the button", async () => {
    const user = userEvent.setup();
    renderNode(<PractitionerAdmin />, new RevokeSpy());
    await open(user, "Youssef Adel");

    const first = clinics().getAllByRole("button", { name: /^remove$/i })[0];
    await user.click(first);

    // "Appointments already booked are not cancelled" is the fact the decision turns on. It used to be a
    // caption above the list, which is to say it was read after the click rather than before it.
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/already booked are not cancelled/i)).toBeInTheDocument();
  });
});
