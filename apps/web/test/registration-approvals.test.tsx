import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { Role } from "../src/authz/permissions";
import { RegistrationApprovals } from "../src/screens/BeneficiaryPortal";
import { seedSession } from "./helpers";

/**
 * US-003 — the registration approval worklist. Until this screen existed the approval endpoints were
 * UI-less: nothing created applications, nobody could verify documents or bind coverage, and the only
 * activation path was the status screen. The workflow this pins:
 *
 *   officer prepares (documents verified, coverage bound) → supervisor decides → approve issues MRS-M-*.
 *
 * The officer/supervisor split is SoD: the person who vouched for the documents must not be the one who
 * activates the member. The hiding here is cosmetic (§6) — the server refuses an officer's hand-crafted
 * decision with urn:hbmp:approver-required — but the screen must still tell each role the truth about
 * which half is theirs.
 *
 * The queue paginates at ten, so a test that wants a specific row SEARCHES for it rather than assuming it is
 * on the first page. That is also how the screen is used, and it means the search box is exercised by every
 * test rather than by one.
 */

function renderAs(role: Role, client: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession(role);
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={client}>
      <RegistrationApprovals />
    </AppProviders>,
  );
}

const rowOf = async (name: RegExp) =>
  (await screen.findAllByRole("row")).find((r) => within(r).queryByText(name))!;

/** Narrow the queue to one person, then hand back their row. */
async function findRow(name: string) {
  const search = await screen.findByRole("searchbox");
  await userEvent.clear(search);
  await userEvent.type(search, name);
  return await rowOf(new RegExp(name, "i"));
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("The worklist", () => {
  it("shows each application's workflow state distinctly, including 'not started'", async () => {
    renderAs("beneficiary_mgmt");
    expect(within(await findRow("Omar Khaled")).getByText(/in review/i)).toBeInTheDocument();
    expect(within(await findRow("Rania Mostafa")).getByText(/info requested/i)).toBeInTheDocument();
    // Registered before applications were auto-created: still on the queue — a person the queue cannot
    // show is a person nobody reviews.
    expect(within(await findRow("Karim Fawzy")).getByText(/not started/i)).toBeInTheDocument();
  });

  it("says WHEN the application was filed and WHO filed it", async () => {
    // Both are new columns, and the officer is not decoration: a request for more information is delivered
    // to exactly this person, so the queue has to be able to name them.
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");
    expect(within(omar).getByText(/18 Jun 2026/)).toBeInTheDocument();
    expect(within(omar).getByText(/Layla Hassan/)).toBeInTheDocument();
  });

  it("names an application nobody is recorded as having filed, rather than leaving it blank", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "registrationWorklist").mockResolvedValue({
      total: 1,
      items: [{
        beneficiary: {
          id: "BEN-L", givenName: "Legacy", familyName: "Record",
          status: { kind: "info", label: { en: "Pending", ar: "قيد الانتظار" } }, statusRaw: "Pending",
          identifiers: [],
        },
        registration: {
          id: "REG-L", status: "Pending", documentsVerified: false, coverageBound: false, notes: null,
          createdAt: "2026-05-02T09:00:00Z", createdBy: null, createdByName: null,
          threadCount: 0, enrolment: null, standingNotes: [],
        },
      }],
    });
    renderAs("beneficiary_mgmt", client);
    // "Unknown", not an empty cell: this is the state in which a RequestInfo decision has nowhere to go.
    expect(within(await rowOf(/Legacy Record/)).getByText(/unknown/i)).toBeInTheDocument();
  });

  it("filters by application state, and the filter says how many each option holds", async () => {
    renderAs("beneficiary_mgmt");
    await screen.findByRole("searchbox");

    const infoChip = screen.getByRole("button", { name: /info requested/i });
    // The count is what makes a filter worth pressing — "Info requested · 2" is a number to manage against.
    expect(infoChip.textContent).toMatch(/2/);
    await userEvent.click(infoChip);

    expect(await rowOf(/Rania Mostafa/)).toBeTruthy();
    expect((await screen.findAllByRole("row")).some((r) => within(r).queryByText(/Omar Khaled/))).toBe(false);
  });

  it("pages the queue and says how much of it is on screen", async () => {
    renderAs("beneficiary_mgmt");
    // Twelve fixture rows at ten per page.
    expect(await screen.findByText(/showing 1–10 of 12/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /next/i }));
    expect(await screen.findByText(/showing 11–12 of 12/i)).toBeInTheDocument();
  });

  it("sorts the WHOLE queue by filing date, not just the page on screen", async () => {
    // The bug this pins: a table that sorts itself sorts the rows it was handed, so reversing the date on
    // page 1 would leave the newest application on page 2 and still look like it worked.
    renderAs("beneficiary_mgmt");
    await screen.findByRole("searchbox");

    await userEvent.click(screen.getByRole("button", { name: /registered$/i }));   // → descending
    const firstRow = (await screen.findAllByRole("row"))[1]!;
    expect(within(firstRow).getByText(/Ziad Kamel/)).toBeInTheDocument();          // filed 27 Jul, the newest
  });

  it("offers Start review exactly where there is no open application", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "createRegistration");
    renderAs("beneficiary_mgmt", client);

    const karim = await findRow("Karim Fawzy");
    await userEvent.click(within(karim).getByRole("button", { name: /start review/i }));
    expect(spy).toHaveBeenCalledWith("BEN-7", expect.any(String));

    // And nowhere else: an open application must not be restartable from the queue.
    expect(within(await findRow("Omar Khaled")).queryByRole("button", { name: /start review/i })).toBeNull();
  });
});

describe("The officer's half — preparation", () => {
  it("records an approval guard when toggled", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setRegistrationChecks");
    renderAs("beneficiary_mgmt", client);

    const hala = await findRow("Hala Zaki");   // coverage ✓, documents ✗
    const docs = within(hala).getByRole("checkbox", { name: /documents verified/i });
    expect(docs).not.toBeChecked();
    await userEvent.click(docs);
    expect(spy).toHaveBeenCalledWith("REG-5", { documentsVerified: true });
  });

  it("shows the officer WHO decides — once, not once per row", async () => {
    // The sentence is a fact about the WORKLIST, not about any row in it. Printed per row it filled the
    // widest column of the screen with identical copies of the same paragraph.
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");
    expect(within(omar).queryByRole("button", { name: /^decide/i })).toBeNull();
    expect(screen.getAllByText(/decisions are made by a beneficiary-management supervisor/i)).toHaveLength(1);
  });

  it("gives the officer the view action too — the actions column is never empty for a whole role", async () => {
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");
    expect(within(omar).getByRole("button", { name: /view registration/i })).toBeInTheDocument();
  });
});

describe("Notes", () => {
  it("opens the conversation in a modal rather than printing prose in the row", async () => {
    renderAs("beneficiary_mgmt");
    const rania = await findRow("Rania Mostafa");
    // The note itself is NOT in the row — it is free text that used to double the row height and still
    // truncate. What the row carries is how many entries there are to read.
    expect(within(rania).queryByText(/UNHCR letter is expired/i)).toBeNull();

    await userEvent.click(within(rania).getByRole("button", { name: /open notes/i }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/UNHCR letter is expired/i)).toBeInTheDocument();
    // A ruling and an answer to one are labelled differently.
    expect(within(dialog).getByText(/appointment at UNHCR/i)).toBeInTheDocument();
    expect(within(dialog).getAllByText(/^decision$/i).length).toBeGreaterThan(0);
  });

  it("lets the officer answer, and the answer joins the thread", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "replyToRegistration");
    renderAs("beneficiary_mgmt", client);

    const rania = await findRow("Rania Mostafa");
    await userEvent.click(within(rania).getByRole("button", { name: /open notes/i }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.type(within(dialog).getByLabelText(/add a note/i), "New letter uploaded today.");
    await userEvent.click(within(dialog).getByRole("button", { name: /add note/i }));

    expect(spy).toHaveBeenCalledWith("REG-2", "New letter uploaded today.");
    expect(await within(dialog).findByText(/new letter uploaded today/i)).toBeInTheDocument();
  });

  it("offers no reply box on a closed application — the server would refuse it", async () => {
    renderAs("beneficiary_mgmt");
    const ahmed = await findRow("Ahmed Sherif");   // Rejected
    await userEvent.click(within(ahmed).getByRole("button", { name: /open notes/i }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByLabelText(/add a note/i)).toBeNull();
    expect(within(dialog).getByText(/this application is closed/i)).toBeInTheDocument();
  });

  it("distinguishes 'no notes' from 'notes you have not opened'", async () => {
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");    // threadCount 0
    expect(within(omar).queryByRole("button", { name: /open notes/i })).toBeNull();
    expect(within(omar).getByText(/no notes yet/i)).toBeInTheDocument();
  });
});

describe("The registration detail", () => {
  it("shows the form as it was filed, including the coverage elected at the desk", async () => {
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");
    await userEvent.click(within(omar).getByRole("button", { name: /view registration/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("MF-04821")).toBeInTheDocument();          // card number
    expect(within(dialog).getByText(/NationalID/)).toBeInTheDocument();
    expect(within(dialog).getByText("PLAN-MERSAL")).toBeInTheDocument();       // elected plan
    expect(within(dialog).getByText(/10%/)).toBeInTheDocument();               // member share
  });

  it("names a withheld clinical note instead of hiding that it exists", async () => {
    // Capture is not disclosure: beneficiary management types slot 1 and does not read it back. Dropping the
    // slot would read as "no diagnosis recorded", which is the one wrong answer.
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");
    await userEvent.click(within(omar).getByRole("button", { name: /view registration/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/known diagnosis/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/your role cannot read it/i)).toBeInTheDocument();
  });

  it("lists the paperwork on file", async () => {
    renderAs("beneficiary_mgmt");
    const omar = await findRow("Omar Khaled");
    await userEvent.click(within(omar).getByRole("button", { name: /view registration/i }));

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText(/card copy/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/personal document/i)).toBeInTheDocument();
  });
});

describe("The supervisor's half — decision", () => {
  it("blocks Approve until both guards hold, and says which are missing", async () => {
    renderAs("beneficiary_mgmt_supervisor");
    const nour = await findRow("Nour Abdelrahman");   // docs ✓, coverage ✗
    await userEvent.click(within(nour).getByRole("button", { name: /^decide/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("radio", { name: /approve/i })).toBeDisabled();
    expect(within(dialog).getByText(/documents verified and coverage bound/i)).toBeInTheDocument();
    // The other two decisions stay available — an incomplete application can still be bounced or refused.
    expect(within(dialog).getByRole("radio", { name: /request information/i })).toBeEnabled();
    expect(within(dialog).getByRole("radio", { name: /reject/i })).toBeEnabled();
  });

  it("tells the supervisor that Request information reaches somebody", async () => {
    // The one decision whose effect happens where the supervisor cannot see it. Without saying so, the note
    // is written as if into a void.
    renderAs("beneficiary_mgmt_supervisor");
    const nour = await findRow("Nour Abdelrahman");
    await userEvent.click(within(nour).getByRole("button", { name: /^decide/i }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/is notified and can reply here/i)).toBeInTheDocument();
  });

  it("requires notes for Request information — they go back to the officer", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "decideRegistration");
    renderAs("beneficiary_mgmt_supervisor", client);

    const nour = await findRow("Nour Abdelrahman");
    await userEvent.click(within(nour).getByRole("button", { name: /^decide/i }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.click(within(dialog).getByRole("radio", { name: /request information/i }));
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(await within(dialog).findByText(/notes are required/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByLabelText(/^notes$/i), "need the current UNHCR letter");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(spy).toHaveBeenCalledWith("REG-4", "RequestInfo", "need the current UNHCR letter");
  });

  it("announces the issued member number on approve — it goes on the card", async () => {
    renderAs("beneficiary_mgmt_supervisor");
    const omar = await findRow("Omar Khaled");   // both guards held
    await userEvent.click(within(omar).getByRole("button", { name: /^decide/i }));

    const dialog = await screen.findByRole("dialog");
    // Approve pre-selected when the guards hold.
    expect(within(dialog).getByRole("radio", { name: /approve/i })).toBeChecked();
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    expect(await screen.findByText(/MRS-M-2026-000418/)).toBeInTheDocument();
  });

  it("renders the server's refusal in the dialog and keeps the work", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "decideRegistration").mockRejectedValue(
      new ApiError("http", "forbidden", 403, {
        title: "approver-required",
        detail: "registration decisions are made by a beneficiary-management supervisor (US-003)",
        type: "urn:hbmp:approver-required",
      }),
    );
    renderAs("beneficiary_mgmt_supervisor", client);

    const omar = await findRow("Omar Khaled");
    await userEvent.click(within(omar).getByRole("button", { name: /^decide/i }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("radio", { name: /reject/i }));
    await userEvent.type(within(dialog).getByLabelText(/^notes$/i), "not eligible under current criteria");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    const alert = await within(dialog).findByRole("alert");
    expect(alert.textContent).toMatch(/supervisor/i);
    expect(within(dialog).getByLabelText(/^notes$/i)).toHaveValue("not eligible under current criteria");
  });
});

describe("Deciding many at once", () => {
  it("is offered to the supervisor only", async () => {
    renderAs("beneficiary_mgmt");
    await screen.findByRole("searchbox");
    expect(screen.queryByRole("checkbox", { name: /decide —/i })).toBeNull();
  });

  it("decides every selected registration and reports what happened", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "decideRegistrations");
    renderAs("beneficiary_mgmt_supervisor", client);
    await screen.findByRole("searchbox");

    const omar = await rowOf(/Omar Khaled/);
    const yara = await rowOf(/Yara Selim/);
    await userEvent.click(within(omar).getByRole("checkbox", { name: /decide —/i }));
    await userEvent.click(within(yara).getByRole("checkbox", { name: /decide —/i }));

    expect(await screen.findByText(/2 selected/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /decide selected/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/decide 2 registrations/i)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    expect(spy).toHaveBeenCalledWith(["REG-1", "REG-7"], "Approve", undefined);
    expect(await screen.findByText(/2 decisions recorded/i)).toBeInTheDocument();
  });

  it("keeps the rows the server refused, with the server's own reason", async () => {
    // A partial result is the normal case and has to be actionable: "8 approved, 2 refused because coverage
    // is not bound" tells the supervisor what to do; "bulk decision failed" does not.
    renderAs("beneficiary_mgmt_supervisor");
    await screen.findByRole("searchbox");

    // Both guards hold on both rows, so the client lets them through; the fixture server refuses REG-9,
    // which is what a bulk refusal actually looks like — state that changed under the page.
    const omar = await rowOf(/Omar Khaled/);
    const sara = await rowOf(/Sara Gamal/);
    await userEvent.click(within(omar).getByRole("checkbox", { name: /decide —/i }));
    await userEvent.click(within(sara).getByRole("checkbox", { name: /decide —/i }));
    await userEvent.click(screen.getByRole("button", { name: /decide selected/i }));

    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    expect(await within(dialog).findByText(/1 recorded, 1 refused/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/no policy\/coverage is bound/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/Sara Gamal/)).toBeInTheDocument();
  });

  it("cannot enlist a row there is no decision to take on", async () => {
    renderAs("beneficiary_mgmt_supervisor");
    await screen.findByRole("searchbox");
    // Karim has no application at all; Ahmed's is already Rejected. A checkbox that ticks and then does
    // nothing is worse than one that will not tick.
    const karim = await findRow("Karim Fawzy");
    expect(within(karim).getByRole("checkbox", { name: /decide —/i })).toBeDisabled();
  });
});
