import { useCallback, useEffect, useState } from "react";
// API_BASE ALREADY ENDS IN /api/v1 (see config.ts). Every URL here appended a second /api/v1, so the
// branch calls hit /api/v1/api/v1/me/branches → 404 → the hook's fail-soft path → no switcher, ever.
// The whole branch-scoping feature (14.2/21.3) was invisible in the app for this one reason.
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import { ACTIVE_BRANCH_HEADER, setActiveBranch } from "../api/activeBranch";
import type { BranchOption } from "./BranchSwitcher";

/**
 * Operational roles that are branch-scoped (design 37 §3). Everyone else is member-scoped (all branches).
 *
 * 25.1 — `branch_manager` and `clinic_manager` used to appear here and in libs/authz's copy of this set.
 * Both were PHANTOMS: named in code, never seeded as identity roles, never held by anyone. Replaced by the
 * one seeded spelling, `branch_coordinator`.
 */
const BRANCH_SCOPED = new Set(["reception", "appointment_coordinator", "nurse", "doctor", "branch_coordinator"]);

/**
 * 25.1 (design 42 §1) — roles that reach a SET of branches at once. The clinics manager supervises all six
 * clinics, so their branch control FILTERS (clearing it restores all six) where a coordinator's SWITCHES.
 * Mirrors `BranchScopeModes.BranchSetScopedRoles`; the server is authoritative either way — this set only
 * decides how the control behaves, never what the caller may reach.
 */
const BRANCH_SET_SCOPED = new Set(["clinics_manager"]);

/**
 * Does this role reach a SET of clinics at once?
 *
 * Exported so a screen can ask without re-listing the roles. The Clinic Management screens need it for one
 * thing only: a write that could apply to any of six clinics has to name the one it means, so the roster form
 * shows a branch picker to a manager and does not to a coordinator, whose branch is decided by the header.
 *
 * <b>This is a REACH question, never an authority one.</b> Both roles hold exactly the same permission set —
 * that is the invariant this whole portal rests on (design 42 §1) — and the server decides what either may
 * actually touch. Nothing here grants anything; it decides whether a control appears.
 */
export function isSetScopedRole(role: string | undefined): boolean {
  return !!role && BRANCH_SET_SCOPED.has(role);
}

interface BranchContextValue {
  memberScoped: boolean;
  /**
   * 25.1 — true for a set-scoped caller (clinics manager). The control filters instead of switching, and a
   * null `activeBranchId` means "all branches in reach", not "unresolved".
   */
  setScoped: boolean;
  branches: BranchOption[];
  activeBranchId: string | null;
  switchBranch: (id: string) => void;
  /**
   * 18.C1 (W2) — the branch the SERVER says is active, echoed back on the switch response. Until the echo
   * arrives this is null and the switcher shows the optimistic local choice. If the two disagree the server
   * wins and the switcher says so: a silent divergence here means the user is reading one branch's worklist
   * while believing they are in another.
   */
  confirmedBranchId: string | null;
}

function authHeaders(json = false): Record<string, string> {
  const token = getToken();
  return { ...(json ? { "Content-Type": "application/json" } : {}), ...(token ? { Authorization: `Bearer ${token}` } : {}) };
}

/**
 * How many times to ask before giving up, and how long to wait between attempts.
 *
 * ============================================================================================================
 * WHY A RETRY, WHEN THE READ IS "FAIL-SOFT"
 * ============================================================================================================
 * It resolved ONCE and gave up on any failure, silently — so a single 401 while the token was still being
 * exchanged, one gateway timeout, or identity-service answering a moment late left the caller with NO branch
 * switcher for the rest of the session. Reported as "the branch selector is flaky: sometimes it shows,
 * sometimes it doesn't", which is exactly what an un-retried request looks like from the outside.
 *
 * The cost of that is not a missing convenience. A clinics manager reaches six clinics and has no active
 * branch until they filter; the switcher IS the filter, so without it they cannot narrow anything — and a
 * branch-scoped write, which the server refuses unless it names a clinic, becomes impossible from a screen
 * that offers no other way to name one.
 *
 * Fail-soft is still right for the case it was written for — the fixture harness, where there is no gateway
 * and `/me/branches` will never answer. The distinction is between a request that FAILED and an answer that
 * was EMPTY: a 200 listing no branches is a real answer and is not retried.
 */
const ATTEMPTS = 4;
const BACKOFF_MS = [400, 1200, 3000];

/**
 * Phase 14.8 — resolves the caller's branch context for the app-bar switcher (design 37 §7). Retries a
 * failed resolve a few times and then stops; a successful resolve that lists no branches simply renders no
 * switcher (e.g. the dev/test harness with no gateway). Member-scoped roles are never branch-restricted here.
 */
export function useBranchContext(role: string | undefined): BranchContextValue {
  const setScoped = !!role && BRANCH_SET_SCOPED.has(role);
  const memberScoped = !role || !(BRANCH_SCOPED.has(role) || setScoped);
  const [branches, setBranches] = useState<BranchOption[]>([]);
  const [activeBranchId, setActiveBranchId] = useState<string | null>(null);
  const [confirmedBranchId, setConfirmedBranchId] = useState<string | null>(null);

  useEffect(() => {
    if (memberScoped) return;
    let live = true;

    /** One attempt. True when the branch set was resolved; false when it should be tried again. */
    const attempt = async (): Promise<boolean> => {
      const [meRes, allRes] = await Promise.all([
        fetch(`${API_BASE}/me/branches`, { headers: authHeaders() }),
        fetch(`${API_BASE}/branches`, { headers: authHeaders() }),
      ]);
      if (!meRes.ok || !allRes.ok) return false;

      const me = await meRes.json();
      const all: Array<{ branchId: string; nameEn: string }> = await allRes.json();
      if (!live) return true;

      const names = new Map(all.map((b) => [b.branchId, b.nameEn]));
      const home: string | null = me.homeBranch ?? null;
      const opts: BranchOption[] = (me.permittedBranches ?? []).map((id: string) => ({
        id, name: names.get(id) ?? id.slice(0, 8), isHome: id === home,
      }));
      setBranches(opts);
      // 25.1 — a SET-scoped caller starts with NO filter, so their first request carries no
      // X-Active-Branch and the server answers for all six clinics. Defaulting them to Home would open a
      // supervisory worklist showing one sixth of its rows, with nothing on screen to say so.
      const initial = setScoped ? null : (home ?? opts[0]?.id ?? null);
      setActiveBranchId(initial);
      setConfirmedBranchId(initial);
      // Publish to the API layer so every subsequent request carries X-Active-Branch.
      setActiveBranch(initial);
      return true;
    };

    (async () => {
      for (let i = 0; i < ATTEMPTS && live; i++) {
        // A thrown fetch (offline, DNS, CORS) and a non-2xx are the same thing here: nothing was resolved.
        let ok = false;
        try { ok = await attempt(); } catch { ok = false; }
        if (ok || !live) return;
        const wait = BACKOFF_MS[Math.min(i, BACKOFF_MS.length - 1)];
        await new Promise((r) => setTimeout(r, wait));
      }
    })();

    return () => {
      live = false;
    };
  }, [memberScoped, setScoped, role]);

  const switchBranch = useCallback((id: string) => {
    setActiveBranchId(id);
    setConfirmedBranchId(null);        // unconfirmed until the server answers
    setActiveBranch(id);               // subsequent requests carry the new branch immediately
    void (async () => {
      try {
        const res = await fetch(`${API_BASE}/me/active-branch`, {
          method: "POST",
          headers: authHeaders(true),
          body: JSON.stringify({ branchId: id }),
        });
        if (!res.ok) {
          // 403 = the branch is outside the caller's permitted set (and the server audited the attempt).
          // Roll back rather than leaving the API layer sending a header the server will keep refusing.
          setActiveBranchId((prev) => (prev === id ? confirmedBranchId : prev));
          setActiveBranch(confirmedBranchId);
          return;
        }
        // Prefer the server's echo — header first, then body — over our own optimistic value.
        const echoed = res.headers.get(ACTIVE_BRANCH_HEADER)
          ?? ((await res.json().catch(() => null)) as { activeBranchId?: string } | null)?.activeBranchId
          ?? id;
        setConfirmedBranchId(echoed);
        setActiveBranchId(echoed);
        setActiveBranch(echoed);
      } catch {
        /* fail-soft: keep the optimistic selection; the next request either works or 403s visibly */
      }
    })();
  }, [confirmedBranchId]);

  return { memberScoped, setScoped, branches, activeBranchId, switchBranch, confirmedBranchId };
}
