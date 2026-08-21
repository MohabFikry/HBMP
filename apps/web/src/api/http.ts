import type { z } from "zod";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import { activeBranchHeader } from "./activeBranch";

/** The RFC 7807 `application/problem+json` fields a service returns on a 4xx/5xx, when it supplies them. */
export interface ProblemDetails {
  title?: string;
  detail?: string;
  type?: string;
  traceId?: string;
}

/**
 * A normalised API failure. `kind` lets screens branch: `network` (offline/timeout), `http` (a 4xx/5xx with
 * a problem body), `schema` (the response did not match the contract — a real defect we surface loudly
 * rather than rendering garbage), or `aborted` (WE cancelled it). For `http` failures the parsed
 * {@link ProblemDetails} are attached so the UI can show the service's own `detail`/`title` (per CLAUDE.md:
 * RFC 7807 problem+json) instead of a generic "request failed".
 *
 * `aborted` is separate from `network` on purpose. Both are "no response arrived", but one is a fault the
 * operator should be told about and the other is this application doing exactly what it meant to — the user
 * typed another character, or left the screen. Folding a cancellation into `network` would put "could not
 * reach the server, check your connection" on screen every time somebody backspaces in a search box.
 */
export class ApiError extends Error {
  constructor(
    readonly kind: "network" | "http" | "schema" | "aborted",
    message: string,
    readonly status?: number,
    readonly problem?: ProblemDetails,
  ) {
    super(message);
    this.name = "ApiError";
  }

  /** The most specific human-readable reason available: server `detail` → `title` → the generic message. */
  get reason(): string {
    return this.problem?.detail ?? this.problem?.title ?? this.message;
  }
}

/**
 * Read an RFC 7807 problem body off a failed response, tolerating a non-JSON or unreadable body (returns
 * `undefined` so the caller falls back to a generic message). Only `application/(problem+)json` is parsed.
 */
async function readProblem(res: Response): Promise<ProblemDetails | undefined> {
  const ct = res.headers.get("content-type") ?? "";
  if (!/application\/(problem\+)?json/i.test(ct)) return undefined;
  try {
    const b = (await res.json()) as Record<string, unknown>;
    const str = (v: unknown) => (typeof v === "string" && v.length > 0 ? v : undefined);
    const problem: ProblemDetails = {
      title: str(b.title),
      detail: str(b.detail),
      type: str(b.type),
      traceId: str(b.traceId) ?? str(b.traceID),
    };
    return problem.title || problem.detail || problem.type || problem.traceId ? problem : undefined;
  } catch {
    return undefined;
  }
}

/** Parse `data` against `schema`, converting a zod failure into a loud `ApiError("schema")`. */
export function parseOr<T>(schema: z.ZodType<T>, data: unknown): T {
  const r = schema.safeParse(data);
  if (!r.success) {
    throw new ApiError("schema", `Response failed contract validation: ${r.error.issues[0]?.message ?? "unknown"}`);
  }
  return r.data;
}

/**
 * ============================================================================================================
 * CANCELLATION (2026-08-09 audit §2.5)
 * ============================================================================================================
 * Every verb below takes an optional `AbortSignal`. There was none, so nothing this application started could
 * ever be stopped: a search box firing on a 250ms debounce left one request per pause in flight against the
 * master-data catalogue, and leaving a screen mid-load kept its reads running to completion for a component
 * that had already unmounted.
 *
 * This was never a CORRECTNESS bug and it should not be described as one — `useAsync` and each typeahead
 * already discard a result whose run has been superseded, so a late response has never been rendered over a
 * newer one. What was missing is the ability to stop the work at all, which is why the fix belongs in the
 * transport rather than in the screens.
 *
 * A cancelled request raises `ApiError("aborted")`, which the hooks swallow. See the class above for why that
 * is not `network`.
 */
async function request(
  path: string,
  init: RequestInit,
  absolute = false,
  signal?: AbortSignal,
): Promise<unknown> {
  let res: Response;
  const token = getToken();
  const auth: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
  // 18.C1 (W2) — the active branch travels with EVERY request. Phase 14 built the whole branch-scoping
  // mechanism and this header was the one missing link, so the switcher changed nothing.
  const branch = activeBranchHeader();
  // For FormData bodies, let the browser set `Content-Type` (with the multipart boundary) itself.
  const isForm = typeof FormData !== "undefined" && init.body instanceof FormData;
  const baseHeaders: Record<string, string> = isForm
    ? { Accept: "application/json", ...auth, ...branch }
    : { "Content-Type": "application/json", Accept: "application/json", ...auth, ...branch };
  try {
    res = await fetch(absolute ? path : `${API_BASE}${path}`, {
      ...init,
      headers: { ...baseHeaders, ...(init.headers ?? {}) },
      signal,
    });
  } catch (e) {
    if (isAbort(e)) throw new ApiError("aborted", `Request to ${path} was cancelled`);
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  if (!res.ok) {
    const problem = await readProblem(res);
    const msg = problem?.detail ?? problem?.title ?? `Request to ${path} failed`;
    throw new ApiError("http", msg, res.status, problem);
  }
  return res.status === 204 ? null : await res.json();
}

/**
 * Did `fetch` reject because we aborted it? `AbortError` is what every browser and undici raise; jsdom and
 * some polyfills raise a plain `Error` with the same name, so the NAME is what is checked rather than
 * `instanceof DOMException`. `signal.aborted` is checked too, for a runtime that reports the abort some other
 * way — if we asked for the cancellation, the cancellation is the explanation.
 */
function isAbort(e: unknown): boolean {
  return (e instanceof Error && e.name === "AbortError")
    || (typeof e === "object" && e !== null && (e as { name?: string }).name === "AbortError");
}

export function getJson<T>(path: string, schema: z.ZodType<T>, signal?: AbortSignal): Promise<T> {
  return request(path, { method: "GET" }, false, signal).then((d) => parseOr(schema, d));
}

/**
 * 18.C2 (W5) — a GET against an ABSOLUTE gateway path, i.e. one outside the `/api/v1` prefix baked into
 * API_BASE. Only `/identity/*` needs this today: the issuer's own admin surface is not a versioned domain
 * API. Identical auth, branch-header and RFC-7807 handling — it differs only in how the URL is built, so a
 * caller cannot accidentally bypass the error contract by reaching for `fetch`.
 */
export function getAbsolute(url: string, signal?: AbortSignal): Promise<unknown> {
  return request(url, { method: "GET" }, /* absolute */ true, signal);
}

export function postAbsolute(url: string, body: unknown, signal?: AbortSignal): Promise<unknown> {
  return request(url, { method: "POST", body: JSON.stringify(body) }, true, signal);
}

/**
 * 21.6 — DELETE against an absolute gateway path. The membership admin screens revoke a single session on
 * `/identity/admin/users/{id}/sessions/{sid}`, which is outside `/api/v1` for the same reason the GET above
 * is: the issuer's own admin surface is not a versioned domain API. Routed through `request` so a revoke
 * that fails still comes back as RFC-7807 — a session revoke reporting a false success is the one outcome
 * this must not produce.
 */
export function deleteAbsolute(url: string, signal?: AbortSignal): Promise<unknown> {
  return request(url, { method: "DELETE" }, true, signal);
}

/**
 * Raw GET/POST that return the untyped body. Used by {@link HttpApiClient} when a service's response shape
 * differs from the shared contract (e.g. a service emits a plain string where the portal contract wants a
 * bilingual object): the client maps the raw body to the contract shape, then validates that mapping. This
 * keeps the service and the screens/contracts each unchanged, with the adapter living at the integration seam.
 */
export function getRaw(path: string, signal?: AbortSignal): Promise<unknown> {
  return request(path, { method: "GET" }, false, signal);
}

/**
 * A GET that also reports `X-Total-Count`.
 *
 * <p>Some list endpoints cap their page and say how many rows matched in the header. The body shape is
 * unchanged, so every existing caller of {@link getRaw} keeps working; a caller that needs to tell the user
 * "showing 200 of 314" reaches for this one instead. `total` falls back to the page length when the header is
 * absent, which is the truth for an endpoint that does not cap.</p>
 */
export async function getRawCounted(
  path: string, signal?: AbortSignal,
): Promise<{ body: unknown; total: number | null }> {
  let res: Response;
  const token = getToken();
  const auth: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
  const branch = activeBranchHeader();
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method: "GET",
      headers: { "Content-Type": "application/json", Accept: "application/json", ...auth, ...branch },
      signal,
    });
  } catch (e) {
    if (isAbort(e)) throw new ApiError("aborted", `Request to ${path} was cancelled`);
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  if (!res.ok) {
    const problem = await readProblem(res);
    throw new ApiError("http", problem?.detail ?? problem?.title ?? `Request to ${path} failed`, res.status, problem);
  }
  const raw = res.headers.get("X-Total-Count");
  const total = raw === null ? null : Number.parseInt(raw, 10);
  return {
    body: res.status === 204 ? null : await res.json(),
    total: total === null || Number.isNaN(total) ? null : total,
  };
}
/**
 * A POST whose response is a FILE, not JSON.
 *
 * <p>The finance export is the only one. It has always returned `text/csv` through `Results.File`, and the
 * client called {@link postRaw}, which parses JSON — so the Exports screen reported a row count and handed
 * the operator nothing. There was no `Blob`, no object URL and no anchor click anywhere in the application.
 * An export delivers a file; a row count is a receipt.</p>
 *
 * <p>Three things come back. The <b>blob</b>, which is the deliverable. The <b>filename</b>, read from
 * `Content-Disposition` so the download is named by the server that knows what it produced rather than by a
 * template the client guesses — the old client built `${report}-${from}_${to}.${format}` locally, which is
 * how it managed to name a file `.xlsx` that was always CSV. And the <b>row count</b>, from `X-Row-Count`,
 * because a file body has nowhere to put it.</p>
 *
 * <p>`rowCount` is null when the header is absent — a gateway that has not been told to expose it is
 * indistinguishable from a server that did not send it, and both mean "unknown", never zero. The download
 * does not depend on it.</p>
 *
 * <p>An error response is still `application/problem+json`, so failures read exactly as they do everywhere
 * else in this module.</p>
 */
export async function postForFile(
  path: string,
  body: unknown,
  signal?: AbortSignal,
): Promise<{ blob: Blob; filename: string | null; rowCount: number | null }> {
  let res: Response;
  const token = getToken();
  const auth: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
  const branch = activeBranchHeader();
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...auth, ...branch },
      body: JSON.stringify(body),
      signal,
    });
  } catch (e) {
    if (isAbort(e)) throw new ApiError("aborted", `Request to ${path} was cancelled`);
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  if (!res.ok) {
    const problem = await readProblem(res);
    throw new ApiError("http", problem?.detail ?? problem?.title ?? `Request to ${path} failed`, res.status, problem);
  }
  const raw = res.headers.get("X-Row-Count");
  const rowCount = raw === null ? null : Number.parseInt(raw, 10);
  return {
    blob: await res.blob(),
    filename: filenameFrom(res.headers.get("Content-Disposition")),
    rowCount: rowCount === null || Number.isNaN(rowCount) ? null : rowCount,
  };
}

/**
 * The filename out of a `Content-Disposition`, or null.
 *
 * <p>`filename*=UTF-8''…` first, because that is the encoding a non-ASCII name arrives in and the plain
 * `filename=` beside it is the ASCII fallback the server degraded to. Null rather than a guess when the
 * header is missing or unparseable: the caller names the download itself in that case, and a wrong name on a
 * financial export is a file somebody later cannot identify.</p>
 */
function filenameFrom(header: string | null): string | null {
  if (!header) return null;
  const extended = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (extended?.[1]) {
    try { return decodeURIComponent(extended[1].trim()); } catch { /* fall through to the plain form */ }
  }
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain?.[1]?.trim() || null;
}

export function postRaw(
  path: string,
  body: unknown,
  idempotencyKey?: string,
  opts?: { ifMatch?: string | number; signal?: AbortSignal },
): Promise<unknown> {
  const headers: Record<string, string> = {};
  if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
  // Optimistic-concurrency opt-in (emr appointment transitions): echo the row version we read as the ETag.
  if (opts?.ifMatch !== undefined && opts.ifMatch !== null) headers["If-Match"] = `"${opts.ifMatch}"`;
  return request(path, { method: "POST", body: JSON.stringify(body), headers }, false, opts?.signal);
}

/**
 * PUT / DELETE with the same auth, branch-header and RFC-7807 handling as the rest of this module (19.6 —
 * the policy administration surface replaces a draft's rule set wholesale and revokes tier assignments, and
 * neither verb existed here). An idempotency key is accepted on PUT because a replaced rule set is a write
 * whose retry must not be a second review.
 */
export function putRaw(
  path: string, body: unknown, idempotencyKey?: string, signal?: AbortSignal,
): Promise<unknown> {
  const headers: Record<string, string> = {};
  if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
  return request(path, { method: "PUT", body: JSON.stringify(body), headers }, false, signal);
}
export function deleteRaw(path: string, signal?: AbortSignal): Promise<unknown> {
  return request(path, { method: "DELETE" }, false, signal);
}
/** PATCH with the same auth/branch/RFC-7807 handling — the registration checks are a partial update, and
 *  expressing one as PUT would demand the caller echo state it does not own. */
export function patchRaw(path: string, body: unknown, signal?: AbortSignal): Promise<unknown> {
  return request(path, { method: "PATCH", body: JSON.stringify(body) }, false, signal);
}

/**
 * GET a non-JSON body (a CSV export). Kept separate from {@link getRaw} rather than sniffing the content
 * type, because a JSON endpoint that starts answering with text is a defect the caller should see, not
 * something to absorb. Errors take the same RFC-7807 path — an export that fails must be as legible as a
 * write that fails.
 */
export async function getText(path: string, signal?: AbortSignal): Promise<string> {
  const token = getToken();
  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method: "GET",
      headers: {
        Accept: "text/csv, text/plain",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...activeBranchHeader(),
      },
      signal,
    });
  } catch (e) {
    if (isAbort(e)) throw new ApiError("aborted", `Request to ${path} was cancelled`);
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  if (!res.ok) {
    const problem = await readProblem(res);
    throw new ApiError("http", problem?.detail ?? problem?.title ?? `Request to ${path} failed`, res.status, problem);
  }
  return await res.text();
}

/**
 * GET a BINARY body — a signed report, a study, a scan.
 *
 * Separate from {@link getText} for the same reason that one is separate from {@link getRaw}: a caller that
 * wants bytes and a caller that wants a string want different failures. Errors take the same RFC-7807 path,
 * so a refused download is as legible as a refused write — which matters most here, because the refusal a
 * clinician meets is usually the 14.7 gate telling them to request access rather than anything broken.
 *
 * Returns a Blob for the caller to hand to the browser. There is no `<a download href>` anywhere near this:
 * an anchor sends no Authorization header, so behind the gateway it is a 401 rendered as a broken download.
 */
export async function getBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const token = getToken();
  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method: "GET",
      headers: {
        Accept: "application/octet-stream, application/pdf, image/*",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...activeBranchHeader(),
      },
      signal,
    });
  } catch (e) {
    if (isAbort(e)) throw new ApiError("aborted", `Request to ${path} was cancelled`);
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  if (!res.ok) {
    const problem = await readProblem(res);
    throw new ApiError("http", problem?.detail ?? problem?.title ?? `Request to ${path} failed`, res.status, problem);
  }
  return await res.blob();
}

/**
 * POST a multipart/form-data body (e.g. a lab/imaging result with an optional file). We deliberately pass no
 * `Content-Type` so the browser sets the multipart boundary itself; the JSON default is overridden to undefined.
 */
export function postForm(
  path: string,
  fields: Record<string, string | Blob>,
  // 18.D1 (U1): a result upload is a clinical write. It had no idempotency key at all, so an operator
  // retrying after a timeout uploaded the result twice.
  idempotencyKey?: string,
  signal?: AbortSignal,
): Promise<unknown> {
  const form = new FormData();
  for (const [k, v] of Object.entries(fields)) form.append(k, v);
  const headers: Record<string, string> = {};
  if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
  return request(path, { method: "POST", body: form, headers }, false, signal);
}

/**
 * POST with an optional `Idempotency-Key` header (per CLAUDE.md API conventions: consume/dispense/decide
 * must not double-apply). The key is BOTH a header and part of the body so a relay/retry maps to one row.
 */
export function postJson<T>(
  path: string,
  body: unknown,
  schema: z.ZodType<T>,
  idempotencyKey?: string,
  signal?: AbortSignal,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
  return request(path, { method: "POST", body: JSON.stringify(body), headers }, false, signal)
    .then((d) => parseOr(schema, d));
}

/** A v4-ish UUID for idempotency keys (crypto.randomUUID where available, deterministic fallback otherwise). */
export function newIdempotencyKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx".replace(/[x]/g, () => Math.floor(Math.random() * 16).toString(16));
}
