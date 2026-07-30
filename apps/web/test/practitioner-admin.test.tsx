import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import type { CreatePractitionerInput, PractitionerAttachFailure } from "@mersal/contracts";
import { PractitionerAdmin } from "../src/screens/PractitionerAdmin";

/**
 * Phase 14.5 — creating a doctor (design 37 §4).
 *
 * The cases worth writing here are the two the screen exists for: that a clinician cannot be created without
 * the two fields booking filters on, and that a PARTIAL create — practitioner row written, an assignment
 * refused — is reported as itself rather than as either success or failure.
 */
class PractitionerApi extends DevApiClient {
  created: CreatePractitionerInput[] = [];
  incomplete: PractitionerAttachFailure[] = [];
  failCreate = false;

  override createPractitioner(input: CreatePractitionerInput) {
    this.created.push(input);
    if (this.failCreate) return Promise.reject(new Error("boom"));
    return Promise.resolve({
      practitioner: {
        id: "PRC-NEW",
        practitionerType: input.practitionerType,
        name: { en: input.fullNameEn, ar: input.fullNameAr },
        primarySpecialty: input.primarySpecialtyCode,
        specialties: [input.primarySpecialtyCode],
        branches: input.branchIds,
        status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } },
      },
      incomplete: this.incomplete,
    });
  }
}

async function choose(user: ReturnType<typeof userEvent.setup>, name: RegExp, option: RegExp) {
  await user.click(await screen.findByRole("combobox", { name }));
  await user.click(screen.getByRole("option", { name: option }));
}

/** Fill everything the form demands, so each test can then remove exactly one thing. */
async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
  await choose(user, /user account/i, /Dr\. Hala/i);
  await user.type(screen.getByLabelText(/full name \(english\)/i), "Nadia Farouk");
  await user.type(screen.getByLabelText(/full name \(arabic\)/i), "نادية فاروق");
  await choose(user, /primary specialty/i, /Pediatrics/i);
  await user.click(screen.getByRole("checkbox", { name: /Dokki/i }));
}

describe("Practitioner admin (14.5) — doctor account creation", () => {
  it("marks a clinician with no specialty or clinic as NOT bookable", async () => {
    renderNode(<PractitionerAdmin />);
    // PRC-4 in the fixture has neither. That combination is invisible to the booking picker's query, so the
    // roster has to say so here — it is the only place anyone would find out before reception cannot.
    const row = (await screen.findByText("Karim Fouad")).closest("tr")!;
    expect(within(row).getByText(/not bookable/i)).toBeInTheDocument();

    const ok = (await screen.findByText("Hana Mansour")).closest("tr")!;
    expect(within(ok).getByText(/^bookable$/i)).toBeInTheDocument();
  });

  it("resolves specialty and branch CODES to names in the roster", async () => {
    renderNode(<PractitionerAdmin />);
    const row = (await screen.findByText("Hana Mansour")).closest("tr")!;
    // "PED" / "BR-DOK" are wire values; an administrator reads names.
    expect(within(row).getByText("Pediatrics")).toBeInTheDocument();
    expect(within(row).getByText(/Dokki · Maadi/)).toBeInTheDocument();
  });

  it("refuses to submit without a specialty — the field booking filters on", async () => {
    const user = userEvent.setup();
    const api = new PractitionerApi({ latencyMs: 0 });
    renderNode(<PractitionerAdmin />, api as unknown as ApiClient);
    await screen.findByText("Hana Mansour");

    await choose(user, /user account/i, /Dr\. Hala/i);
    await user.type(screen.getByLabelText(/full name \(english\)/i), "Nadia Farouk");
    await user.type(screen.getByLabelText(/full name \(arabic\)/i), "نادية فاروق");
    await user.click(screen.getByRole("checkbox", { name: /Dokki/i }));
    await user.click(screen.getByRole("button", { name: /create clinician/i }));

    expect(await screen.findByText(/choose a primary specialty/i)).toBeInTheDocument();
    expect(api.created).toHaveLength(0);
  });

  it("refuses to submit without a clinic — the other field booking filters on", async () => {
    const user = userEvent.setup();
    const api = new PractitionerApi({ latencyMs: 0 });
    renderNode(<PractitionerAdmin />, api as unknown as ApiClient);
    await screen.findByText("Hana Mansour");

    await choose(user, /user account/i, /Dr\. Hala/i);
    await user.type(screen.getByLabelText(/full name \(english\)/i), "Nadia Farouk");
    await user.type(screen.getByLabelText(/full name \(arabic\)/i), "نادية فاروق");
    await choose(user, /primary specialty/i, /Pediatrics/i);
    await user.click(screen.getByRole("button", { name: /create clinician/i }));

    expect(await screen.findByText(/choose at least one clinic/i)).toBeInTheDocument();
    expect(api.created).toHaveLength(0);
  });

  it("sends the account, specialty and every chosen clinic", async () => {
    const user = userEvent.setup();
    const api = new PractitionerApi({ latencyMs: 0 });
    renderNode(<PractitionerAdmin />, api as unknown as ApiClient);
    await screen.findByText("Hana Mansour");

    await fillValidForm(user);
    await user.click(screen.getByRole("checkbox", { name: /Maadi/i }));   // a doctor at two clinics
    await user.click(screen.getByRole("button", { name: /create clinician/i }));

    await waitFor(() => expect(api.created).toHaveLength(1));
    const sent = api.created[0];
    expect(sent.primarySpecialtyCode).toBe("PED");
    expect(sent.branchIds).toEqual(["BR-DOK", "BR-MAA"]);
    expect(sent.userId).toBeTruthy();
    expect(sent.fullNameAr).toBe("نادية فاروق");
    expect(await screen.findByText(/clinician created/i)).toBeInTheDocument();
  });

  /**
   * The case the whole `incomplete` contract exists for. The practitioner row was written and the clinic
   * assignment was refused — so "created" is a lie that leaves an unbookable doctor behind, and "failed" is a
   * lie that invites a resubmit which 409s on the unique user id.
   */
  it("reports a partial create as itself — names the failure, withholds the success chip, keeps the form", async () => {
    const user = userEvent.setup();
    const api = new PractitionerApi({ latencyMs: 0 });
    api.incomplete = [{ step: "branch", ref: "BR-DOK", reason: "assignment already exists" }];
    renderNode(<PractitionerAdmin />, api as unknown as ApiClient);
    await screen.findByText("Hana Mansour");

    await fillValidForm(user);
    await user.click(screen.getByRole("button", { name: /create clinician/i }));

    // Says what did not save, in names rather than ids, with the service's own reason.
    const alert = await screen.findByText(/part of the assignment did not save/i);
    const box = alert.closest("div")!;
    expect(within(box).getByText(/Dokki/)).toBeInTheDocument();
    expect(within(box).getByText(/assignment already exists/)).toBeInTheDocument();
    // And tells the operator the one thing they must not do.
    expect(within(box).getByText(/do not submit the form again/i)).toBeInTheDocument();

    // No success chip — this did not fully succeed.
    expect(screen.queryByText(/^clinician created$/i)).not.toBeInTheDocument();
    // The form keeps its contents: the operator needs them to finish the assignment.
    expect(screen.getByLabelText(/full name \(english\)/i)).toHaveValue("Nadia Farouk");
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderNode(<PractitionerAdmin />);
    await screen.findByText("Hana Mansour");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

/**
 * The edit panel. These cover the three rules the client does NOT re-implement and therefore must not
 * contradict: the primary specialty cannot be removed, promoting one demotes the other, and a repaired
 * record stops being flagged unbookable.
 */
describe("Practitioner admin — amending an existing clinician", () => {
  /**
   * Select a roster row; the panel renders beside the table. Scoped to the TABLE because once the panel is
   * open the clinician's name appears twice on the page — in their row and as the panel's heading.
   */
  // role="grid", not "table": DataTable switches role when `interactive` is set, so that `aria-selected` on
  // the current row is valid ARIA rather than silently ignored.
  const rosterRow = async (name: string) =>
    (await within(await screen.findByRole("grid")).findByText(name)).closest("tr")!;

  async function open(user: ReturnType<typeof userEvent.setup>, name: string) {
    await user.click(await rosterRow(name));
    return screen.getByRole("heading", { level: 2, name });
  }

  /**
   * The panel's blocks, by their accessible name. Scoping matters here: the roster beside the panel prints
   * the same specialty and clinic names, so an unscoped `getByText("Cardiology")` matches both and an
   * unscoped `Add` button matches the specialty one and the clinic one.
   */
  const specialtiesRegion = () => within(screen.getByRole("region", { name: /^specialties$/i }));
  const clinicsRegion = () => within(screen.getByRole("region", { name: /^clinics$/i }));
  const statusRegion = () => within(screen.getByRole("region", { name: /^status$/i }));

  it("offers no Remove on the primary specialty — the server refuses it, so the button must not exist", async () => {
    const user = userEvent.setup();
    renderNode(<PractitionerAdmin />);
    await open(user, "Youssef Adel");   // primary CARD, secondary GP

    const cardio = specialtiesRegion().getByText("Cardiology").closest("li")!;
    expect(within(cardio).getByText(/primary/i)).toBeInTheDocument();
    expect(within(cardio).queryByRole("button", { name: /remove/i })).not.toBeInTheDocument();
    expect(within(cardio).queryByRole("button", { name: /make primary/i })).not.toBeInTheDocument();

    // The secondary carries both actions.
    const gp = specialtiesRegion().getByText("General Practice").closest("li")!;
    expect(within(gp).getByRole("button", { name: /remove/i })).toBeInTheDocument();
    expect(within(gp).getByRole("button", { name: /make primary/i })).toBeInTheDocument();
  });

  it("promoting a specialty demotes the previous primary", async () => {
    const user = userEvent.setup();
    renderNode(<PractitionerAdmin />);
    await open(user, "Youssef Adel");

    const gp = specialtiesRegion().getByText("General Practice").closest("li")!;
    await user.click(within(gp).getByRole("button", { name: /make primary/i }));

    await waitFor(() => {
      const nowPrimary = specialtiesRegion().getByText("General Practice").closest("li")!;
      expect(within(nowPrimary).getByText(/primary/i)).toBeInTheDocument();
    });
    // And the old one is demoted — it now offers the actions only a secondary has.
    const cardio = specialtiesRegion().getByText("Cardiology").closest("li")!;
    expect(within(cardio).getByRole("button", { name: /make primary/i })).toBeInTheDocument();
  });

  it("repairs the unbookable record — first specialty added becomes primary, and the roster updates", async () => {
    const user = userEvent.setup();
    renderNode(<PractitionerAdmin />);
    // Karim has neither specialty nor clinic.
    await open(user, "Karim Fouad");
    expect(specialtiesRegion().getByText(/no specialty assigned/i)).toBeInTheDocument();
    expect(clinicsRegion().getByText(/no clinic assigned/i)).toBeInTheDocument();

    await choose(user, /add a specialty/i, /Dermatology/i);
    await user.click(specialtiesRegion().getByRole("button", { name: /^add$/i }));
    // Added as PRIMARY, not as a secondary that would leave him unbookable with nothing explaining why.
    await waitFor(() => {
      const derm = specialtiesRegion().getByText("Dermatology").closest("li")!;
      expect(within(derm).getByText(/primary/i)).toBeInTheDocument();
    });

    await choose(user, /add a clinic/i, /Aswan/i);
    await user.click(clinicsRegion().getByRole("button", { name: /^add$/i }));

    // The roster's Bookable column is the point of the repair.
    await waitFor(async () => {
      expect(within(await rosterRow("Karim Fouad")).getByText(/^bookable$/i)).toBeInTheDocument();
    });
  });

  it("warns before removing a clinician's only clinic", async () => {
    const user = userEvent.setup();
    renderNode(<PractitionerAdmin />);
    await open(user, "Youssef Adel");   // assigned to Nasr City only
    expect(clinicsRegion().getByText(/this is their only clinic/i)).toBeInTheDocument();

    // And states the consequence the server cannot: existing appointments are not cancelled.
    expect(clinicsRegion().getByText(/appointments already booked are not cancelled/i)).toBeInTheDocument();
  });

  it("requires a reason before changing status", async () => {
    const user = userEvent.setup();
    renderNode(<PractitionerAdmin />);
    await open(user, "Hana Mansour");

    await user.click(statusRegion().getByRole("button", { name: /^apply$/i }));
    expect(await screen.findByText(/a reason is required/i)).toBeInTheDocument();
  });

  it("panel has no serious/critical a11y violations", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<PractitionerAdmin />);
    await open(user, "Hana Mansour");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});
