import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { PolicyList } from "../src/screens/PolicyBook";
import { BulkJobs } from "../src/screens/PolicyBulk";
import { ApiError } from "../src/api/http";
import type { BulkJobView, PolicyApi, PolicyQueryRow } from "../src/api/policyApi";

/**
 * A page that shows a SUBSET has to say so.
 *
 * <b>The defect these pin.</b> Three tables consumed a deliberately-capped server response and rendered the
 * rows without the field that bounds them:
 *
 * <ul>
 *   <li>`PolicyList` asked for `pageSize: 50` and dropped `totalCount`, `totalPages` and
 *       `identityMatchTruncated`. Policy 51 was unreachable and nothing on screen said so — an operator
 *       searching for it was told, in effect, that it does not exist.</li>
 *   <li>`BulkJobs` and `BatchIntake` rendered the first 50 errors and dropped `totalErrors`. A 3,000-error
 *       file showed fifty rows with nothing after them, which reads as "these are the errors"; fixing those
 *       fifty and re-uploading fails again for the other 2,950.</li>
 * </ul>
 *
 * In both cases the server was already doing the right thing and the SPA was discarding the evidence — which
 * is why these are asserted against the RENDER rather than against the request. A test that only checked the
 * fetch parameters would have passed throughout the bug.
 */

// ── Fakes ────────────────────────────────────────────────────────────────────────────────────────────────

const reject = () => Promise.reject(new ApiError("network", "not stubbed in this test"));

/** The most recent call's first argument. `.at()` is ES2022 and this package targets lower. */
const lastArg = (fn: { mock: { calls: unknown[][] } }) =>
  fn.mock.calls[fn.mock.calls.length - 1]?.[0];

/** Only the members of `PolicyApi` these screens touch; the rest reject loudly if a screen reaches for them. */
function fakeApi(overrides: Partial<PolicyApi>): PolicyApi {
  return new Proxy({ ...overrides } as PolicyApi, {
    get: (target, prop) => (prop in target ? target[prop as keyof PolicyApi] : reject),
  });
}

function policyRow(n: number): PolicyQueryRow {
  return {
    policyId: `pol-${n}`,
    policyNo: `POL-${String(n).padStart(4, "0")}`,
    payerId: null,
    status: "Active",
    effectiveFrom: "2026-01-01",
    effectiveTo: null,
    memberCount: n,
    memberCountBand: "Small",
    maxMembers: null,
    planCount: 1,
    totalLimit: null,
    totalConsumed: null,
    percentUsed: null,
    utilizationBand: "Low",
  };
}

function policyPage(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    items: [policyRow(1)],
    page: 1,
    pageSize: 25,
    totalCount: 1,
    totalPages: 1,
    sortedBy: "policyno",
    payerScopeApplied: false,
    identityMatchTruncated: false,
    unavailable: [],
    ...overrides,
  };
}

// ── PolicyList ───────────────────────────────────────────────────────────────────────────────────────────

describe("the policy book renders the whole result, not the first page of it", () => {
  it("drives a pager from totalCount rather than from the rows it happens to hold", async () => {
    const policyQuery = vi.fn(() =>
      Promise.resolve(policyPage({ totalCount: 210, totalPages: 9 }) as never));
    renderNode(<PolicyList api={fakeApi({ policyQuery })} />);

    expect(await screen.findByText("POL-0001")).toBeInTheDocument();

    // The count the operator is owed. One row is on screen; 210 exist.
    await waitFor(() => expect(screen.getByText(/210/)).toBeInTheDocument());
    // And a control that can reach them. Before the fix there was no pager at all.
    expect(screen.getByRole("navigation", { name: /pagination/i })).toBeInTheDocument();
  });

  it("asks the server for the next page instead of paging rows it already has", async () => {
    const policyQuery = vi.fn(() =>
      Promise.resolve(policyPage({ totalCount: 210, totalPages: 9 }) as never));
    renderNode(<PolicyList api={fakeApi({ policyQuery })} />);
    await screen.findByText("POL-0001");

    const calls = policyQuery.mock.calls.length;
    await userEvent.click(screen.getByRole("button", { name: /next/i }));

    // A refetch, with page 2 asked of the SERVER. Slicing the 25 rows in hand would be the bug the
    // `useTableQuery` docs describe, arriving through a different door.
    await waitFor(() => expect(policyQuery.mock.calls.length).toBeGreaterThan(calls));
    expect(lastArg(policyQuery)).toMatchObject({ page: 2 });
  });

  it("sorts on the SERVER, in the server's own vocabulary", async () => {
    const policyQuery = vi.fn(() => Promise.resolve(policyPage() as never));
    renderNode(<PolicyList api={fakeApi({ policyQuery })} />);
    await screen.findByText("POL-0001");

    // "Members" — `membercount` in PolicySortFields.Allowed.
    await userEvent.click(screen.getByRole("button", { name: /members/i }));
    await waitFor(() =>
      expect(lastArg(policyQuery)).toMatchObject({ sort: "membercount" }));

    // Same header again flips direction, and the server's "-" prefix carries it.
    await userEvent.click(screen.getByRole("button", { name: /members/i }));
    await waitFor(() =>
      expect(lastArg(policyQuery)).toMatchObject({ sort: "-membercount" }));
  });

  it("only offers the orders the server accepts", async () => {
    const policyQuery = vi.fn(() => Promise.resolve(policyPage() as never));
    renderNode(<PolicyList api={fakeApi({ policyQuery })} />);
    await screen.findByText("POL-0001");

    // `plancount` is not in PolicySortFields.Allowed, so the header must not be a sort button — one that
    // promised the order would answer with a 400 and an UNKNOWN_SORT_FIELD problem.
    expect(screen.queryByRole("button", { name: /^plans$/i })).not.toBeInTheDocument();
  });

  it("says so when the identity match was truncated", async () => {
    const policyQuery = () =>
      Promise.resolve(policyPage({ identityMatchTruncated: true }) as never);
    renderNode(<PolicyList api={fakeApi({ policyQuery })} />);

    expect(await screen.findByText(/this page is a subset/)).toBeInTheDocument();
  });

  it("stays quiet when nothing was truncated — the alert must mean something", async () => {
    const policyQuery = () => Promise.resolve(policyPage() as never);
    renderNode(<PolicyList api={fakeApi({ policyQuery })} />);
    await screen.findByText("POL-0001");

    expect(screen.queryByText(/this page is a subset/)).not.toBeInTheDocument();
  });
});

// ── Bulk intake ──────────────────────────────────────────────────────────────────────────────────────────

const JOB: BulkJobView = {
  jobId: "job-1",
  jobType: "MemberEnrolment",
  fileName: "members.csv",
  status: "Validated",
  batchId: "b1",
  totalRows: 3000,
  validRows: 0,
  invalidRows: 3000,
  appliedRows: 0,
  failedRows: 0,
  skippedRows: 0,
  balances: true,
  errorDocumentId: "doc-9",
  submittedAt: "2026-08-10T09:00:00Z",
} as BulkJobView;

/** Upload a file and press Validate — the shortest real path to the error and preview tables. */
async function uploadAndValidate(
  errors: unknown[],
  totalErrors: number,
  job: BulkJobView = JOB,
  preview: { wouldChange: unknown[]; totalWouldChange: number } = { wouldChange: [], totalWouldChange: 0 },
) {
  const api = fakeApi({
    bulkTemplates: () => Promise.resolve([]),
    uploadBulk: () => Promise.resolve(job),
    validateBulk: () =>
      Promise.resolve({ job, totalErrors, errors, ...preview, committable: false } as never),
  });
  renderNode(<BulkJobs api={api} />);

  const input = await screen.findByLabelText(/file/i);
  await userEvent.upload(input, new File(["a,b\n1,2"], "members.csv", { type: "text/csv" }));
  await userEvent.click(screen.getByRole("button", { name: /upload/i }));
  await userEvent.click(await screen.findByRole("button", { name: /validate/i }));
}

const rowError = (n: number) => ({
  rowNumber: n,
  code: "BAD_PLAN",
  detailEn: `Row ${n} names a plan that does not exist`,
  detailAr: `الصف ${n}`,
});

describe("the bulk error list reports its own size", () => {
  it("says how many of the total it is showing", async () => {
    // What the server actually returns for a 3,000-error file: the first 50, and the real count.
    await uploadAndValidate(Array.from({ length: 50 }, (_, i) => rowError(i + 1)), 3000);

    expect(await screen.findByText(/Showing the first 50 of 3,000 errors/)).toBeInTheDocument();
    // And points at the report that holds the rest, at the moment the whole list is wanted.
    expect(screen.getByText(/error report contains member data/)).toBeInTheDocument();
  });

  it("stays quiet when the list is complete", async () => {
    await uploadAndValidate([rowError(1), rowError(2)], 2);

    expect(await screen.findAllByText(/does not exist/)).toHaveLength(2);
    expect(screen.queryByText(/Showing the first/)).not.toBeInTheDocument();
  });

  it("reports the size of the CHANGE preview too, which is capped the same way", async () => {
    // The preview is capped at the same 50 and, until the server was given a total for it, the SPA had no
    // way to know: "What this file would change" showed fifty rows of a thousand-row change with nothing
    // saying so — on the step whose entire purpose is seeing what the file is about to do.
    await uploadAndValidate([], 0, JOB, {
      wouldChange: Array.from({ length: 50 }, (_, i) => ({
        rowNumber: i + 1, summaryEn: `Row ${i + 1} moves to Plan B`, summaryAr: "", changes: {},
      })),
      totalWouldChange: 1000,
    });

    expect(await screen.findByText(/Showing the first 50 of 1,000 changes/)).toBeInTheDocument();
  });

  it("stays quiet when the preview is complete", async () => {
    await uploadAndValidate([], 0, JOB, {
      wouldChange: [{ rowNumber: 1, summaryEn: "Row 1 moves to Plan B", summaryAr: "", changes: {} }],
      totalWouldChange: 1,
    });

    expect(await screen.findByText(/moves to Plan B/)).toBeInTheDocument();
    expect(screen.queryByText(/Showing the first/)).not.toBeInTheDocument();
  });

  it("does not promise an error report that was never written", async () => {
    await uploadAndValidate(
      Array.from({ length: 50 }, (_, i) => rowError(i + 1)),
      3000,
      { ...JOB, errorDocumentId: null } as BulkJobView);

    expect(await screen.findByText(/Showing the first 50 of 3,000 errors/)).toBeInTheDocument();
    expect(screen.queryByText(/error report contains member data/)).not.toBeInTheDocument();
  });
});
