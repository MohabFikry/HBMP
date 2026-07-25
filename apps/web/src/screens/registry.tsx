import { lazy, type ReactNode } from "react";

/**
 * Maps a fully-qualified route path → its wired flagship screen (Phase 9.3). Each screen is `React.lazy` so
 * it code-splits into its own chunk — a reception user never downloads the approvals or dashboard bundle
 * (14-navigation-structure: portals are code-split). A section without an entry here still renders the 9.2
 * `SectionPage` stub, so the portal stays navigable while screens land incrementally.
 */
const ReceptionEligibility = lazy(() =>
  import("./ReceptionEligibility").then((m) => ({ default: m.ReceptionEligibility })),
);
const DoctorEncounter = lazy(() => import("./DoctorEncounter").then((m) => ({ default: m.DoctorEncounter })));
const LabQueue = lazy(() => import("./LabQueue").then((m) => ({ default: m.LabQueue })));
const PharmacyDispense = lazy(() => import("./PharmacyDispense").then((m) => ({ default: m.PharmacyDispense })));
const ApprovalsWorklist = lazy(() => import("./ApprovalsWorklist").then((m) => ({ default: m.ApprovalsWorklist })));
const ExecutiveDashboard = lazy(() => import("./ExecutiveDashboard").then((m) => ({ default: m.ExecutiveDashboard })));

export const SCREENS: Record<string, () => ReactNode> = {
  // 1. Reception — eligibility (also surfaced in the beneficiary-management portal).
  "/reception/eligibility": () => <ReceptionEligibility />,
  "/beneficiaries/eligibility": () => <ReceptionEligibility />,
  // 2. Doctor — consultation / EMR.
  "/clinician/encounter": () => <DoctorEncounter />,
  // 3. Lab / imaging — queue + consume.
  "/lab/queue": () => <LabQueue kind="lab" />,
  "/imaging/queue": () => <LabQueue kind="imaging" />,
  // 4. Pharmacy — dispense (queue + partial dispense).
  "/pharmacy/queue": () => <PharmacyDispense />,
  // 5. Approvals — worklist + decision (US-060).
  "/approvals/worklist": () => <ApprovalsWorklist />,
  // 6. Executive dashboard (US-073) — director scope; finance sees a diagnosis-free finance scope.
  "/director/dashboards": () => <ExecutiveDashboard scope="director" />,
  "/finance/utilization": () => <ExecutiveDashboard scope="finance" />,
};

export function screenFor(fullPath: string): (() => ReactNode) | undefined {
  return SCREENS[fullPath];
}
