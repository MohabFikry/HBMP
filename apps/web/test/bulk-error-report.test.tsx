import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { BulkErrorReportButton } from "../src/screens/BulkErrorReport";
import { clearTokens, setToken } from "../src/auth/tokenStore";

/**
 * The report the app promised and could not fetch.
 *
 * Every part of this existed except the last one. `BulkJobEngine` writes the full row-error list to
 * document-service the moment a job has errors — the list quotes member numbers, so it belongs behind an
 * authorization rather than in a JSON body. document-service serves it from
 * `/operational-documents/{id}/content` through the authorization engine and audits every read as an Export
 * with a `phi` field class. Kong routes it. And the SPA rendered a sentence saying the report "is downloaded
 * through an authorized, audited request" while offering no control that made one.
 *
 * These pin the three things that make the control correct rather than merely present: the token goes with
 * the request, a refusal reads as a refusal, and the control is absent when there is no report.
 */

const ORIGINAL_FETCH = globalThis.fetch;

describe("downloading the bulk error report", () => {
  beforeEach(() => {
    setToken("test-token");
    // jsdom implements neither; the component hands the blob to the browser through both.
    globalThis.URL.createObjectURL = vi.fn(() => "blob:report");
    globalThis.URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => {
    globalThis.fetch = ORIGINAL_FETCH;
    clearTokens();
    vi.restoreAllMocks();
  });

  it("renders nothing when the job produced no report", () => {
    renderNode(<BulkErrorReportButton documentId={null} jobId="job-1" />);
    // A control that 404s is worse than an absent one, and the engine logs a warning when storage failed —
    // so a missing id is a real state, not a rendering accident.
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("fetches the stored report WITH the bearer token", async () => {
    const fetchMock = vi.fn(() =>
      Promise.resolve(new Response("row_number,error_code\n1,BAD_PLAN\n", { status: 200 })));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    renderNode(<BulkErrorReportButton documentId="doc-9" jobId="job-1" />);
    await userEvent.click(screen.getByRole("button", { name: /full error report/i }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toContain("/operational-documents/doc-9/content");
    // The whole reason this is a fetch and not an <a href download>: an anchor sends no Authorization
    // header, and behind the gateway that is a 401 the browser renders as a broken download with no message.
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer test-token");
  });

  it("distinguishes a refusal from an outage", async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(new Response("", { status: 403 }))) as unknown as typeof fetch;

    renderNode(<BulkErrorReportButton documentId="doc-9" jobId="job-1" />);
    await userEvent.click(screen.getByRole("button", { name: /full error report/i }));

    // 403 is the authorization engine, and it says something a retry cannot fix.
    expect(await screen.findByText(/not permitted to download this report/i)).toBeInTheDocument();
    expect(screen.queryByText(/check your connection/i)).not.toBeInTheDocument();
  });

  it("reports a transport failure as one", async () => {
    globalThis.fetch = vi.fn(() => Promise.reject(new Error("offline"))) as unknown as typeof fetch;

    renderNode(<BulkErrorReportButton documentId="doc-9" jobId="job-1" />);
    await userEvent.click(screen.getByRole("button", { name: /full error report/i }));

    expect(await screen.findByText(/check your connection/i)).toBeInTheDocument();
  });
});
