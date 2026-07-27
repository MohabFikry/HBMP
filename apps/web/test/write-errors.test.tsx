import { describe, expect, it } from "vitest";
import { renderHook, act } from "@testing-library/react";
import { ApiError } from "../src/api/http";
import { writeErrorMessage } from "../src/api/writeError";
import { useWrite } from "../src/api/useWrite";

/**
 * Phase 18.D1 (audit R2 U1/U2) — a failed mutation must say what happened, and a retry must not duplicate.
 *
 * Four screens caught everything as `catch { setStatus("idle") }`. The spinner stopped, nothing appeared,
 * and the form still held the operator's typing — which reads as "nothing happened". The natural response is
 * to press the button again, and since none of those four sent an idempotency key, the second press created
 * a second beneficiary, a second provider, a second manual authorization, a second result.
 */

describe("U2 — one typed, translated message per failure mode", () => {
  it("distinguishes a conflict from a dropped connection", () => {
    // The specific pairing the audit calls out. Both used to render as nothing at all, and they demand
    // OPPOSITE actions: retrying a 409 duplicates work; reloading after a network blip loses the form.
    const conflict = writeErrorMessage(new ApiError("http", "conflict", 409));
    const network = writeErrorMessage(new ApiError("network", "offline"));

    expect(conflict.action).toBe("reload");
    expect(network.action).toBe("retry");
    expect(conflict.message.en).not.toBe(network.message.en);
  });

  it("maps every status a mutation can return to a distinct action", () => {
    const cases: Array<[number, string]> = [
      [401, "reauth"],   // sign in again — nothing was saved
      [403, "stop"],     // access changed; retrying will fail identically
      [404, "reload"],   // the record is gone
      [409, "reload"],   // someone got there first
      [412, "reload"],   // it changed under you
      [422, "retry"],    // fix the fields and resubmit
      [429, "retry"],    // wait, then retry
      [500, "reload"],   // may or may not have applied
    ];
    for (const [status, action] of cases)
      expect(writeErrorMessage(new ApiError("http", "x", status)).action, `status ${status}`)
        .toBe(action);
  });

  it("flags the cases where the write may ALREADY have applied", () => {
    // This is what stops the UI inviting a blind retry. A 401/403/412/422 definitely did not apply; a
    // network failure or a 5xx might have.
    expect(writeErrorMessage(new ApiError("network", "x")).possiblyApplied).toBe(true);
    expect(writeErrorMessage(new ApiError("http", "x", 500)).possiblyApplied).toBe(true);
    expect(writeErrorMessage(new ApiError("http", "x", 403)).possiblyApplied).toBe(false);
    expect(writeErrorMessage(new ApiError("http", "x", 412)).possiblyApplied).toBe(false);
  });

  it("appends the service's own RFC-7807 detail, which is the part that names the field or rule", () => {
    const e = new ApiError("http", "unprocessable", 422, { detail: "quantity exceeds the remaining amount" });
    expect(writeErrorMessage(e).message.en).toContain("quantity exceeds the remaining amount");
  });

  it("is bilingual, because every operator-facing string on this platform is", () => {
    const m = writeErrorMessage(new ApiError("http", "x", 409)).message;
    expect(m.ar.length).toBeGreaterThan(0);
    expect(m.ar).not.toBe(m.en);
  });

  it("names a schema mismatch as OUR defect and does not invite a retry", () => {
    const e = writeErrorMessage(new ApiError("schema", "contract mismatch"));
    expect(e.action).toBe("stop");
    expect(e.message.en).toMatch(/fault on our side/i);
  });
});

describe("U1 — the idempotency key is minted once and rotates only on confirmed success", () => {
  it("reuses the SAME key across retries of a failed attempt", async () => {
    // The whole point. A key minted per CLICK makes the second press of a button after a timeout a fresh
    // write — which is how a retrying operator creates a duplicate clinical record.
    const seen: string[] = [];
    const { result } = renderHook(() => useWrite());

    await act(async () => {
      await result.current.run((key) => { seen.push(key); return Promise.reject(new ApiError("network", "x")); });
    });
    await act(async () => {
      await result.current.run((key) => { seen.push(key); return Promise.reject(new ApiError("network", "x")); });
    });

    expect(seen).toHaveLength(2);
    expect(seen[0]).toBe(seen[1]);
  });

  it("rotates the key after a success, so the NEXT submission is not a replay of the last", async () => {
    const seen: string[] = [];
    const { result } = renderHook(() => useWrite());

    await act(async () => { await result.current.run((key) => { seen.push(key); return Promise.resolve(); }); });
    await act(async () => { await result.current.run((key) => { seen.push(key); return Promise.resolve(); }); });

    expect(seen[0]).not.toBe(seen[1]);
  });

  it("surfaces the failure instead of swallowing it, and reports success truthfully", async () => {
    const { result } = renderHook(() => useWrite());

    let ok: boolean | undefined;
    await act(async () => { ok = await result.current.run(() => Promise.reject(new ApiError("http", "x", 409))); });
    expect(ok).toBe(false);
    expect(result.current.error?.action).toBe("reload");
    expect(result.current.done).toBe(false);

    await act(async () => { ok = await result.current.run(() => Promise.resolve()); });
    expect(ok).toBe(true);
    expect(result.current.error).toBeNull();
    expect(result.current.done).toBe(true);
  });

  it("clears a previous error when a new attempt starts", async () => {
    const { result } = renderHook(() => useWrite());
    await act(async () => { await result.current.run(() => Promise.reject(new ApiError("http", "x", 422))); });
    expect(result.current.error).not.toBeNull();
    await act(async () => { await result.current.run(() => Promise.resolve()); });
    expect(result.current.error).toBeNull();
  });
});
