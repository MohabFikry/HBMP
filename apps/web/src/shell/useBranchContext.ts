import { useCallback, useEffect, useState } from "react";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import type { BranchOption } from "./BranchSwitcher";

/** Operational roles that are branch-scoped (design 37 §3). Everyone else is member-scoped (all branches). */
const BRANCH_SCOPED = new Set(["reception", "appointment_coordinator", "nurse", "doctor", "branch_manager", "clinic_manager"]);

interface BranchContextValue {
  memberScoped: boolean;
  branches: BranchOption[];
  activeBranchId: string | null;
  switchBranch: (id: string) => void;
}

function authHeaders(json = false): Record<string, string> {
  const token = getToken();
  return { ...(json ? { "Content-Type": "application/json" } : {}), ...(token ? { Authorization: `Bearer ${token}` } : {}) };
}

/**
 * Phase 14.8 — resolves the caller's branch context for the app-bar switcher (design 37 §7). Fail-soft: any
 * fetch error leaves an empty set so the switcher simply doesn't render (e.g. in the dev/test harness with no
 * gateway). Member-scoped roles are never branch-restricted here — the switcher is a convenience only.
 */
export function useBranchContext(role: string | undefined): BranchContextValue {
  const memberScoped = !role || !BRANCH_SCOPED.has(role);
  const [branches, setBranches] = useState<BranchOption[]>([]);
  const [activeBranchId, setActiveBranchId] = useState<string | null>(null);

  useEffect(() => {
    if (memberScoped) return;
    let live = true;
    (async () => {
      try {
        const [meRes, allRes] = await Promise.all([
          fetch(`${API_BASE}/api/v1/me/branches`, { headers: authHeaders() }),
          fetch(`${API_BASE}/api/v1/branches`, { headers: authHeaders() }),
        ]);
        if (!meRes.ok || !allRes.ok) return;
        const me = await meRes.json();
        const all: Array<{ branchId: string; nameEn: string }> = await allRes.json();
        if (!live) return;
        const names = new Map(all.map((b) => [b.branchId, b.nameEn]));
        const home: string | null = me.homeBranch ?? null;
        const opts: BranchOption[] = (me.permittedBranches ?? []).map((id: string) => ({
          id, name: names.get(id) ?? id.slice(0, 8), isHome: id === home,
        }));
        setBranches(opts);
        setActiveBranchId(home ?? opts[0]?.id ?? null);
      } catch {
        /* fail-soft: no switcher */
      }
    })();
    return () => {
      live = false;
    };
  }, [memberScoped, role]);

  const switchBranch = useCallback((id: string) => {
    setActiveBranchId(id);
    void fetch(`${API_BASE}/api/v1/me/active-branch`, {
      method: "POST",
      headers: authHeaders(true),
      body: JSON.stringify({ branchId: id }),
    }).catch(() => {
      /* fail-soft */
    });
  }, []);

  return { memberScoped, branches, activeBranchId, switchBranch };
}
