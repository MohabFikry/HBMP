import type { z } from "zod";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";

/**
 * A normalised API failure. `kind` lets screens branch: `network` (offline/timeout), `http` (a 4xx/5xx with
 * a problem body), or `schema` (the response did not match the contract — a real defect we surface loudly
 * rather than rendering garbage).
 */
export class ApiError extends Error {
  constructor(
    readonly kind: "network" | "http" | "schema",
    message: string,
    readonly status?: number,
  ) {
    super(message);
    this.name = "ApiError";
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

async function request(path: string, init: RequestInit): Promise<unknown> {
  let res: Response;
  const token = getToken();
  const auth: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};
  try {
    res = await fetch(`${API_BASE}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
        ...auth,
        ...(init.headers ?? {}),
      },
    });
  } catch (e) {
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  if (!res.ok) {
    throw new ApiError("http", `Request to ${path} failed`, res.status);
  }
  return res.status === 204 ? null : await res.json();
}

export function getJson<T>(path: string, schema: z.ZodType<T>): Promise<T> {
  return request(path, { method: "GET" }).then((d) => parseOr(schema, d));
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
): Promise<T> {
  const headers: Record<string, string> = {};
  if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
  return request(path, { method: "POST", body: JSON.stringify(body), headers }).then((d) => parseOr(schema, d));
}

/** A v4-ish UUID for idempotency keys (crypto.randomUUID where available, deterministic fallback otherwise). */
export function newIdempotencyKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
  return "xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx".replace(/[x]/g, () => Math.floor(Math.random() * 16).toString(16));
}
