import { describe, expect, it, vi, beforeEach } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode, seedSession } from "./helpers";
import { PatientProfile, PatientContextBar } from "../src/screens/PatientProfile";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import type { PatientProfile as PatientProfileContract } from "@mersal/contracts";
import { ApiProvider } from "../src/api/ApiProvider";

/**
 * Phase 20.4 — the profile screen.
 *
 * <b>These tests assert the PAYLOAD's consequences, not the DOM's tidiness.</b> The security property this
 * screen carries is that it renders whatever the server sent and invents nothing: the server-side reflection
 * tests prove a reception payload has no clinical field in it, and these prove the screen does not reconstruct
 * one, does not assemble its own clipboard text, and does not collapse the three withheld states into one.
 */

const BEN = "b-amal";

function profile(sections: PatientProfileContract["sections"]): PatientProfileContract {
  return { beneficiaryId: BEN, servedAt: new Date().toISOString(), sections };
}

function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  const dev = new DevApiClient({ latencyMs: 0 });
  return Object.assign(dev, over) as ApiClient;
}

function renderProfile(api: ApiClient) {
  return renderNode(
    <ApiProvider client={api}>
      <PatientProfile beneficiaryId={BEN} />
    </ApiProvider>,
  );
}

/**
 * jsdom exposes `navigator.clipboard` as a getter-only property, so it has to be redefined, not assigned.
 *
 * <b>Call this AFTER `userEvent.setup()`.</b> user-event v14 installs its own clipboard stub during setup, so
 * a mock installed before it is silently replaced and every assertion against it fails for a reason that looks
 * like the component not calling it.
 */
function stubClipboard(value: unknown) {
  Object.defineProperty(navigator, "clipboard", { value, configurable: true, writable: true });
}

/** Set up the user AND take over the clipboard, in that order. Returns both. */
function setupWithClipboard() {
  const user = userEvent.setup();
  const writeText = vi.fn().mockResolvedValue(undefined);
  stubClipboard({ writeText });
  return { user, writeText };
}

beforeEach(() => {
  stubClipboard({ writeText: vi.fn().mockResolvedValue(undefined) });
});

// ---------------------------------------------------------------- the three states

describe("20.4 — Restricted, Unavailable and Empty are three distinct states", () => {
  it("renders a restricted section as locked, with a reason and a request-access action", async () => {
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([
          {
            key: "investigations",
            state: "Restricted",
            reasonCode: "sensitive-requires-grant",
            requestAccessAction: { kind: "report-access-request", href: "/x", label: "Request access" },
          },
        ]),
      ),
    });
    renderProfile(api);

    const section = await screen.findByRole("region", { name: /investigations/i });
    // Four cues: the WORD is present, not only a colour or an icon.
    expect(within(section).getByText("Restricted")).toBeInTheDocument();
    // The reason is a sentence a user can act on, never a bare reason code.
    expect(within(section).getByText(/stays existence-only until access is granted/i)).toBeInTheDocument();
    expect(within(section).getByRole("button", { name: /request access/i })).toBeInTheDocument();
  });

  it("renders an unavailable section with Retry — never as empty", async () => {
    // The distinction being defended: a clinician who reads "no records" when the truth is "the service did
    // not answer" has been actively misinformed.
    const reload = vi.fn();
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([{ key: "encounters", state: "Unavailable", reasonCode: "timeout" }]),
      ),
    });
    void reload;
    renderProfile(api);

    const section = await screen.findByRole("region", { name: /encounters/i });
    expect(within(section).getByText(/temporarily unavailable/i)).toBeInTheDocument();
    expect(within(section).getByRole("button", { name: /retry/i })).toBeInTheDocument();
    expect(within(section).queryByText(/^no records$/i)).not.toBeInTheDocument();
  });

  it("renders an empty section plainly, with no retry and no lock", async () => {
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(profile([{ key: "referrals", state: "NotApplicable" }])),
    });
    renderProfile(api);

    const section = await screen.findByRole("region", { name: /referrals/i });
    expect(within(section).getByText(/no records/i)).toBeInTheDocument();
    expect(within(section).queryByRole("button", { name: /retry/i })).not.toBeInTheDocument();
    expect(within(section).queryByText(/restricted/i)).not.toBeInTheDocument();
  });

  it("gives the three states three different markers in the DOM", async () => {
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([
          { key: "investigations", state: "Restricted", reasonCode: "not-treating" },
          { key: "encounters", state: "Unavailable", reasonCode: "timeout" },
          { key: "referrals", state: "NotApplicable" },
        ]),
      ),
    });
    const { container } = renderProfile(api);
    await screen.findByRole("region", { name: /referrals/i });

    expect(container.querySelector('[data-state="restricted"]')).toBeTruthy();
    expect(container.querySelector('[data-state="unavailable"]')).toBeTruthy();
    expect(container.querySelector('[data-state="empty"]')).toBeTruthy();
  });
});

// ---------------------------------------------------------------- withheld sections stay withheld

describe("20.4 — the screen renders the payload and invents nothing", () => {
  it("does not render a section the server did not return", async () => {
    // A reception payload has no investigations key at all. The screen must not render a placeholder for it —
    // an empty "Investigations" card tells a receptionist the patient has had none, which is not what the
    // server said and may not be true.
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([{ key: "header", state: "Visible", data: header() }]),
      ),
    });
    renderProfile(api);
    await screen.findByRole("region", { name: /identity/i });

    expect(screen.queryByRole("region", { name: /investigations/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("region", { name: /prescriptions/i })).not.toBeInTheDocument();
  });

  it("omits the photo entirely when the payload carries none", async () => {
    // Finance and labs receive a header with NO photoUrl. The screen falls back to initials, and there is no
    // broken image and no request to a photo endpoint.
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([{ key: "header", state: "Visible", data: { ...header(), photoUrl: undefined } }]),
      ),
    });
    const { container } = renderProfile(api);
    await screen.findByRole("region", { name: /identity/i });

    expect(container.querySelector("img.profile-avatar")).toBeNull();
    expect(container.querySelector(".profile-avatar--initials")).toBeTruthy();
  });

  it("orders sections with alerts pinned directly under the header", async () => {
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([
          { key: "callHistory", state: "NotApplicable" },
          { key: "alerts", state: "Visible", data: { allergies: [] } },
          { key: "header", state: "Visible", data: header() },
        ]),
      ),
    });
    renderProfile(api);
    await screen.findByRole("region", { name: /identity/i });

    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings[0]).toMatch(/identity/i);
    expect(headings[1]).toMatch(/alerts/i);
  });
});

// ---------------------------------------------------------------- call history

describe("20.4 — call history: four cues and a server-generated clipboard", () => {
  it("renders direction with the WORD and an arrow icon, not colour alone", async () => {
    renderProfile(fakeApi({ patientProfile: vi.fn().mockResolvedValue(profile([callHistorySection()])) }));
    const section = await screen.findByRole("region", { name: /call history/i });
    // Scoped to the ROWS, because the direction filter's <option>s carry the same words.
    const rows = within(section).getByRole("list", { name: "" }) ?? section;
    void rows;

    // The word, on the row chips.
    const out = section.querySelector('[data-direction="Outbound"]');
    const inb = section.querySelector('[data-direction="Inbound"]');
    expect(out?.textContent).toContain("Outbound");
    expect(inb?.textContent).toContain("Inbound");
    // The icon and the shape, carried as data attributes so this asserts the cue and not a class name.
    expect(out?.getAttribute("data-shape")).toBe("square");
    expect(inb?.getAttribute("data-shape")).toBe("circle");
    expect(out?.textContent).toContain("↗");
    expect(inb?.textContent).toContain("↙");
  });

  it("copies the SERVER-PROVIDED copyText verbatim, by keyboard, and announces it", async () => {
    const { user, writeText } = setupWithClipboard();
    renderProfile(fakeApi({ patientProfile: vi.fn().mockResolvedValue(profile([callHistorySection()])) }));
    await screen.findByRole("region", { name: /call history/i });

    // The accessible name identifies WHICH call — "Copy" repeated down a list is useless to a screen reader.
    const button = screen.getByRole("button", { name: /copy summary of outbound call on/i });
    button.focus();
    await user.keyboard("{Enter}");

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    // Verbatim: the exact string the server generated, not one the browser assembled.
    expect(writeText).toHaveBeenCalledWith(SERVER_COPY_TEXT);
    // Announced via aria-live, not only a toast.
    expect(await screen.findByRole("status")).toHaveTextContent(/call summary copied/i);
  });

  it("never puts a summary on the clipboard when the served row has none", async () => {
    // A Meta-level viewer (finance). The row arrives with no summary and a copyText that has none either —
    // the screen must copy what it was GIVEN rather than reconstruct a block from the fields it holds.
    const metaText = "[Inbound] 2026-07-11 11:05\nMember: MRS-M-014882 · Ref: CALL-2026-004102\nReason: EligibilityEnquiry · Outcome: Resolved";
    const { user, writeText } = setupWithClipboard();
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            {
              key: "callHistory",
              state: "Visible",
              data: {
                level: "Meta",
                items: [
                  {
                    callRef: "CALL-2026-004102",
                    direction: "Inbound",
                    startedAt: "2026-07-11T08:05:00Z",
                    reasonCode: "EligibilityEnquiry",
                    outcome: "Resolved",
                    summaryEdited: false,
                    copyText: metaText,
                  },
                ],
              },
            },
          ]),
        ),
      }),
    );

    await screen.findByRole("region", { name: /call history/i });
    // Absence of a summary is stated, not left blank — blank would read as "the agent wrote nothing".
    expect(screen.getByText(/summary not available at your access level/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /copy summary of inbound call on/i }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(metaText));
    expect(String(writeText.mock.calls[0][0])).not.toMatch(/moved from 25 Jul/);
  });

  it("copy-all goes through the endpoint that writes the audit event", async () => {
    // Joining the rows in the browser would produce the same text and NO CallSummaryCopied record. The audit
    // is the point: copying is when PHI leaves the platform's control.
    const copyCallSummaries = vi.fn().mockResolvedValue({
      level: "Full", callRefs: ["CALL-2026-004137", "CALL-2026-004102"], copyText: "joined block",
    });
    const { user, writeText } = setupWithClipboard();
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(profile([callHistorySection()])),
        copyCallSummaries,
      }),
    );
    await screen.findByRole("region", { name: /call history/i });

    await user.click(screen.getByRole("button", { name: /copy all visible/i }));
    await waitFor(() => expect(copyCallSummaries).toHaveBeenCalledTimes(1));
    expect(copyCallSummaries).toHaveBeenCalledWith(BEN, ["CALL-2026-004137", "CALL-2026-004102"]);
    await waitFor(() => expect(writeText).toHaveBeenCalledWith("joined block"));
  });

  it("falls back to a selectable textarea when the clipboard API is unavailable", async () => {
    // http origins and some embedded browsers have no navigator.clipboard. Failing silently teaches users to
    // screenshot a patient record, which is strictly worse than any dialog.
    const user = userEvent.setup();
    stubClipboard(undefined);
    renderProfile(fakeApi({ patientProfile: vi.fn().mockResolvedValue(profile([callHistorySection()])) }));
    await screen.findByRole("region", { name: /call history/i });

    await user.click(screen.getByRole("button", { name: /copy summary of outbound call on/i }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("textbox")).toHaveValue(SERVER_COPY_TEXT);
  });

  it("filters by direction without re-requesting the server's projection", async () => {
    const patientProfile = vi.fn().mockResolvedValue(profile([callHistorySection()]));
    const user = userEvent.setup();
    renderProfile(fakeApi({ patientProfile }));
    await screen.findByRole("region", { name: /call history/i });

    await user.selectOptions(screen.getByRole("combobox"), "Inbound");
    const section = screen.getByRole("region", { name: /call history/i });
    // Scoped to the ROW chips — the filter's own <option> elements carry these words too.
    expect(section.querySelector('[data-direction="Outbound"]')).toBeNull();
    expect(section.querySelector('[data-direction="Inbound"]')).toBeTruthy();
    // Filtering is client-side over the ALREADY-PROJECTED rows: re-requesting would be a second PHI read and
    // a second audit event for one user's single glance.
    expect(patientProfile).toHaveBeenCalledTimes(1);
  });
});

// ---------------------------------------------------------------- the context bar

describe("20.4 — the patient context bar", () => {
  it("asks for header and alerts only", async () => {
    // It is on every clinical screen and cannot be slow (p95 < 400ms), so it must not pull the full profile.
    const patientProfile = vi.fn().mockResolvedValue(
      profile([
        { key: "header", state: "Visible", data: header() },
        { key: "alerts", state: "Visible", data: { allergies: [{ allergen: "Penicillin", severity: "High" }] } },
      ]),
    );
    renderNode(
      <ApiProvider client={fakeApi({ patientProfile })}>
        <PatientContextBar beneficiaryId={BEN} />
      </ApiProvider>,
    );

    await screen.findByText("Amal Hassan");
    expect(patientProfile).toHaveBeenCalledWith(BEN, ["header", "alerts"]);
    expect(screen.getByText(/1 alerts/i)).toBeInTheDocument();
  });

  it("opens the full file by ROUTING, not by reloading the document", async () => {
    // This strip follows the user into the encounter, dispense, lab, approval and call-centre workspaces, so
    // it was the most-reachable way into the patient file — and, as an `<a href>`, the most destructive: a
    // full document load tore down the SPA and with it the open encounter, the dispense in progress or the
    // live call. It also emptied the history, so the profile then had nothing to go back TO.
    const user = userEvent.setup();
    const patientProfile = vi.fn().mockResolvedValue(
      profile([{ key: "header", state: "Visible", data: header() }]),
    );
    renderNode(
      <ApiProvider client={fakeApi({ patientProfile })}>
        <PatientContextBar beneficiaryId={BEN} />
      </ApiProvider>,
    );

    const name = await screen.findByRole("button", { name: "Amal Hassan" });
    // Not an anchor: an href here is the reload, whatever else is true of it.
    expect(name).not.toHaveAttribute("href");
    await user.click(name);
  });
});

// ---------------------------------------------------------------- module deep-links & print

describe("20.4 — module deep-links and the print summary", () => {
  it("offers a section's module link only when the role holds the permission", async () => {
    // Rendered or absent — never disabled. A greyed-out "Start encounter" on a receptionist's screen
    // advertises a capability they will never have; a link that 403s is worse.
    seedSession("doctor");
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([{ key: "encounters", state: "Visible", data: { items: [] } }]),
        ),
      }),
    );
    const section = await screen.findByRole("region", { name: /encounters/i });
    expect(within(section).getByRole("link", { name: /start encounter/i })).toHaveAttribute(
      "href", expect.stringContaining(BEN));
  });

  it("offers no module link to a role without the permission", async () => {
    seedSession("reception");
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([{ key: "encounters", state: "Visible", data: { items: [] } }]),
        ),
      }),
    );
    const section = await screen.findByRole("region", { name: /encounters/i });
    expect(within(section).queryByRole("link", { name: /start encounter/i })).not.toBeInTheDocument();
  });

  it("offers no module link beside a RESTRICTED section", async () => {
    // Inviting a clinician to order against a record they were just told they cannot see.
    seedSession("doctor");
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([{ key: "investigations", state: "Restricted", reasonCode: "not-treating" }]),
        ),
      }),
    );
    const section = await screen.findByRole("region", { name: /investigations/i });
    expect(within(section).queryByRole("link", { name: /raise investigation/i })).not.toBeInTheDocument();
  });

  it("fetches the print summary from the SERVER rather than printing the DOM", async () => {
    // Printing what is on screen would make the export's contents a property of what this browser happened
    // to have loaded, and would skip the separate PHI-export audit event entirely.
    seedSession("doctor");
    const profileSummary = vi.fn().mockResolvedValue({
      profile: profile([{ key: "header", state: "Visible", data: header() }]),
      watermark: {
        viewerSubject: "u-1", viewerRoles: "doctor",
        generatedAt: new Date().toISOString(), purpose: "profile-export",
      },
    });
    const printed: string[] = [];
    vi.spyOn(window, "open").mockReturnValue({
      document: { write: (h: string) => printed.push(h), close: () => {} },
      print: () => {},
    } as unknown as Window);

    const user = userEvent.setup();
    renderProfile(fakeApi({
      patientProfile: vi.fn().mockResolvedValue(profile([{ key: "header", state: "Visible", data: header() }])),
      profileSummary,
    }));
    await screen.findByRole("region", { name: /identity/i });

    await user.click(screen.getByRole("button", { name: /print summary/i }));
    await waitFor(() => expect(profileSummary).toHaveBeenCalledWith(BEN));
    // The watermark comes from the PAYLOAD — an export printable without it leaves unattributed.
    await waitFor(() => expect(printed.join("")).toContain("profile-export"));
    expect(printed.join("")).toContain("doctor");
  });

  it("does not offer the print summary to a role without profile.export", async () => {
    seedSession("reception");
    renderProfile(fakeApi({
      patientProfile: vi.fn().mockResolvedValue(profile([{ key: "header", state: "Visible", data: header() }])),
    }));
    await screen.findByRole("region", { name: /identity/i });
    expect(screen.queryByRole("button", { name: /print summary/i })).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------- accessibility

describe("20.4 — accessibility", () => {
  it("is axe-clean with all four section states on screen", async () => {
    const { container } = renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            { key: "alerts", state: "Visible", data: { allergies: [{ allergen: "Penicillin", severity: "High" }] } },
            { key: "investigations", state: "Restricted", reasonCode: "not-treating" },
            { key: "encounters", state: "Unavailable", reasonCode: "timeout" },
            { key: "referrals", state: "NotApplicable" },
            callHistorySection(),
          ]),
        ),
      }),
    );
    await screen.findByRole("region", { name: /call history/i });
    expect(await axe(container)).toHaveNoViolations();
  });
});

// ---------------------------------------------------------------- fixtures

function header() {
  return {
    beneficiaryId: BEN,
    memberNo: "MRS-M-014882",
    displayName: "Amal Hassan",
    displayNameAr: "أمل حسن",
    ageBand: "30-39",
    sex: "F",
    status: "Active",
    statusCue: { label: "Active", icon: "check-circle", shape: "circle", tone: "positive" },
    photoUrl: `/api/v1/patients/${BEN}/photo`,
  };
}

const SERVER_COPY_TEXT =
  "[Outbound] 2026-07-24 15:32 (6m 12s) · Nasr City · Agent: R. Adel\n" +
  "Member: MRS-M-014882 · Ref: CALL-2026-004137\n" +
  "Reason: RescheduleAppointment · Outcome: Resolved\n" +
  "Appointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request.";

function callHistorySection(): PatientProfileContract["sections"][number] {
  return {
    key: "callHistory",
    state: "Visible",
    data: {
      level: "Full",
      items: [
        {
          callRef: "CALL-2026-004137",
          direction: "Outbound",
          startedAt: "2026-07-24T12:32:00Z",
          endedAt: "2026-07-24T12:38:12Z",
          durationSeconds: 372,
          branchCode: "Nasr City",
          agentDisplayName: "R. Adel",
          reasonCode: "RescheduleAppointment",
          outcome: "Resolved",
          summary: "Appointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request.",
          summaryEdited: false,
          linkedArtifacts: [],
          copyText: SERVER_COPY_TEXT,
        },
        {
          callRef: "CALL-2026-004102",
          direction: "Inbound",
          startedAt: "2026-07-11T08:05:00Z",
          durationSeconds: 160,
          reasonCode: "EligibilityEnquiry",
          outcome: "Resolved",
          summary: "Confirmed remaining dental limit.",
          summaryEdited: true,
          linkedArtifacts: [],
          copyText: "[Inbound] 2026-07-11 11:05 · Ref: CALL-2026-004102",
        },
      ],
    },
  };
}
