import { useEffect, useState } from "react";
import type { Localized } from "@mersal/contracts";
import type { NetworkTierView, PlanView, PolicyApi } from "../api/policyApi";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";

export interface BranchRef {
  branchId: string;
  nameEn: string;
  nameAr?: string;
}

export interface RegistrationReference {
  plans: PlanView[];
  tiers: NetworkTierView[];
  branches: BranchRef[];
  loading: boolean;
  /** Named rather than swallowed — see below. */
  unavailable: Localized | null;
}

/**
 * The three lists the coverage section of the registration form is built from.
 *
 * ============================================================================================================
 * NOT FAIL-SOFT
 * ============================================================================================================
 * The tempting behaviour is to render empty droplists when a lookup fails, so the form still "works". It
 * does not work: plan, tier and contribution are all mandatory, so an operator faced with three empty lists
 * fills in the whole person, presses Register and is refused — having typed everything twice by the time
 * anybody works out that the lists were the problem, not the data.
 *
 * So a failure is NAMED, the form says the coverage section cannot be completed yet, and the operator is told
 * to retry rather than left to infer it from three silent controls.
 *
 * Branches are the exception, and deliberately: the branch is optional (`Default Branch` carries no asterisk),
 * so an unreachable branch directory costs a convenience rather than the registration.
 */
export function useRegistrationReference(api: PolicyApi): RegistrationReference {
  const [plans, setPlans] = useState<PlanView[]>([]);
  const [tiers, setTiers] = useState<NetworkTierView[]>([]);
  const [branches, setBranches] = useState<BranchRef[]>([]);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setLoading(true);
    setUnavailable(null);

    (async () => {
      try {
        const [planRows, tierRows] = await Promise.all([api.plans(), api.networkTiers()]);
        if (!live) return;
        // Only what a member can actually be put ON. A retired plan in the list is an entitlement decision
        // waiting to be made by accident.
        setPlans(planRows.filter((p) => p.status === "Active"));
        setTiers(tierRows.filter((t) => t.status === "Active"));
      } catch {
        if (live) {
          setUnavailable({
            en: "The plan and network-tier lists could not be loaded, so coverage cannot be chosen. Retry before filling the form in.",
            ar: "تعذّر تحميل قوائم الخطط وشرائح الشبكة، لذا لا يمكن اختيار التغطية. أعد المحاولة قبل تعبئة النموذج.",
          });
        }
      }

      // Separate, and allowed to fail quietly: the branch is optional on the form.
      try {
        const token = getToken();
        const res = await fetch(`${API_BASE}/branches`, {
          headers: token ? { Authorization: `Bearer ${token}` } : {},
        });
        if (res.ok && live) setBranches((await res.json()) as BranchRef[]);
      } catch {
        /* the branch field simply offers nothing; it is not required */
      }

      if (live) setLoading(false);
    })();

    return () => {
      live = false;
    };
  }, [api]);

  return { plans, tiers, branches, loading, unavailable };
}
