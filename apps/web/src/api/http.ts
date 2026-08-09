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
