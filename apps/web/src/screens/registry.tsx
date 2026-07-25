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
// Reception desk (Phase 3) — day board, visits, and check-in share one chunk.
const ReceptionVisits = lazy(() => import("./ReceptionDesk").then((m) => ({ default: m.ReceptionVisits })));
const ReceptionAppointments = lazy(() => import("./ReceptionDesk").then((m) => ({ default: m.ReceptionAppointments })));
const ReceptionCheckIn = lazy(() => import("./ReceptionDesk").then((m) => ({ default: m.ReceptionCheckIn })));
const DoctorEncounter = lazy(() => import("./DoctorEncounter").then((m) => ({ default: m.DoctorEncounter })));
// Clinician worklists (Phase 4) — my patients / orders / prescriptions / results inbox share one chunk.
const DoctorPatients = lazy(() => import("./ClinicianWorklists").then((m) => ({ default: m.DoctorPatients })));
const DoctorOrders = lazy(() => import("./ClinicianWorklists").then((m) => ({ default: m.DoctorOrders })));
const DoctorPrescriptions = lazy(() => import("./ClinicianWorklists").then((m) => ({ default: m.DoctorPrescriptions })));
const DoctorResults = lazy(() => import("./ClinicianWorklists").then((m) => ({ default: m.DoctorResults })));
// Nurse portal (Phase 4) — vitals capture + read; patients reuses the clinician worklist.
const NurseVitals = lazy(() => import("./NursePortal").then((m) => ({ default: m.NurseVitals })));
const NurseResults = lazy(() => import("./NursePortal").then((m) => ({ default: m.NurseResults })));
const LabQueue = lazy(() => import("./LabQueue").then((m) => ({ default: m.LabQueue })));
// Lab/imaging result upload (Phase 5.3) — one chunk, parameterised by capability.
const ResultUpload = lazy(() => import("./ResultUpload").then((m) => ({ default: m.ResultUpload })));
const PharmacyDispense = lazy(() => import("./PharmacyDispense").then((m) => ({ default: m.PharmacyDispense })));
// Pharmacy substitutions (Phase 6.3) — formulary lookup of policy-approved alternatives.
const Substitutions = lazy(() => import("./Substitutions").then((m) => ({ default: m.Substitutions })));
const ApprovalsWorklist = lazy(() => import("./ApprovalsWorklist").then((m) => ({ default: m.ApprovalsWorklist })));
// Approvals break-glass + SLA (Phase 7.3) — manual auth / emergency approve / TAT board share one chunk.
const ApprovalsManual = lazy(() => import("./ApprovalsExtra").then((m) => ({ default: m.ApprovalsManual })));
const ApprovalsEmergency = lazy(() => import("./ApprovalsExtra").then((m) => ({ default: m.ApprovalsEmergency })));
const ApprovalsSla = lazy(() => import("./ApprovalsExtra").then((m) => ({ default: m.ApprovalsSla })));
const ExecutiveDashboard = lazy(() => import("./ExecutiveDashboard").then((m) => ({ default: m.ExecutiveDashboard })));
// Director oversight / quality / escalations (Phase 8.3) — one generic report screen, parameterised.
const DirectorReport = lazy(() => import("./ReportView").then((m) => ({ default: m.DirectorReport })));
// Case-manager portal (Phase 10.3) — one chunk for the two case screens.
const MyCases = lazy(() => import("./CaseManager").then((m) => ({ default: m.MyCases })));
const Escalations = lazy(() => import("./CaseManager").then((m) => ({ default: m.Escalations })));
// Finance portal (Phase 10.3) — one chunk for the four finance screens.
const FinanceUtilization = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceUtilization })));
const FinanceSettlements = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceSettlements })));
const FinanceSummaries = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceSummaries })));
const FinanceExports = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceExports })));
// Cross-cutting inbox (Phase 8.1) — one chunk, mounted under every portal's `/…/notifications` route.
const Notifications = lazy(() => import("./Notifications").then((m) => ({ default: m.Notifications })));
// Admin / platform governance (Phase 8b) — one chunk, mounted under both the org-admin (/admin) and
// super-admin (/platform) portal bases which share section paths.
const AdminUsers = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminUsers })));
const AdminPolicies = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminPolicies })));
const AdminTenants = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminTenants })));
const AdminGovernance = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminGovernance })));
const AdminMasterData = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminMasterData })));
const AdminConfig = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminConfig })));

export const SCREENS: Record<string, () => ReactNode> = {
  // 1. Reception — eligibility (also surfaced in the beneficiary-management portal).
  "/reception/eligibility": () => <ReceptionEligibility />,
  "/beneficiaries/eligibility": () => <ReceptionEligibility />,
  "/reception/queue": () => <ReceptionVisits />,
  "/reception/appointments": () => <ReceptionAppointments />,
  "/reception/check-in": () => <ReceptionCheckIn />,
  // 2. Doctor — consultation / EMR + cross-encounter worklists.
  "/clinician/encounter": () => <DoctorEncounter />,
  "/clinician/patients": () => <DoctorPatients />,
  "/clinician/orders": () => <DoctorOrders />,
  "/clinician/prescriptions": () => <DoctorPrescriptions />,
  "/clinician/results": () => <DoctorResults />,
  // 2b. Nurse — my patients (reused) / vitals & triage / results inbox.
  "/nurse/patients": () => <DoctorPatients />,
  "/nurse/vitals": () => <NurseVitals />,
  "/nurse/results": () => <NurseResults />,
  // 3. Lab / imaging — queue + consume.
  "/lab/queue": () => <LabQueue kind="lab" />,
  "/imaging/queue": () => <LabQueue kind="imaging" />,
  "/lab/consume": () => <LabQueue kind="lab" />,
  "/imaging/consume": () => <LabQueue kind="imaging" />,
  "/lab/result": () => <ResultUpload kind="lab" />,
  "/imaging/result": () => <ResultUpload kind="imaging" />,
  // 4. Pharmacy — dispense (queue + partial dispense).
  "/pharmacy/queue": () => <PharmacyDispense />,
  "/pharmacy/dispense": () => <PharmacyDispense />,
  "/pharmacy/substitutions": () => <Substitutions />,
  // 5. Approvals — worklist + decision (US-060).
  "/approvals/worklist": () => <ApprovalsWorklist />,
  "/approvals/manual": () => <ApprovalsManual />,
  "/approvals/emergency": () => <ApprovalsEmergency />,
  "/approvals/sla": () => <ApprovalsSla />,
  // 6. Executive dashboard (US-073) — director scope.
  "/director/dashboards": () => <ExecutiveDashboard scope="director" />,
  "/director/oversight": () => <DirectorReport section="oversight" />,
  "/director/quality": () => <DirectorReport section="quality" />,
  "/director/escalations": () => <DirectorReport section="escalations" />,
  // 7. Case-manager portal (Phase 10.3) — My Cases → coordination-360 (+ tasks); escalations.
  "/cases/my-cases": () => <MyCases />,
  "/cases/beneficiary-360": () => <MyCases />,
  "/cases/escalations": () => <Escalations />,
  // 8. Finance portal (Phase 10.3) — utilization / settlements / summaries / exports. No clinical route exists.
  "/finance/utilization": () => <FinanceUtilization />,
  "/finance/settlements": () => <FinanceSettlements />,
  "/finance/summaries": () => <FinanceSummaries />,
  "/finance/exports": () => <FinanceExports />,
};

// Admin sections are shared by the org-admin (/admin/*) and super-admin (/platform/*) portals; map by the
// trailing section rather than enumerating both bases.
const ADMIN_SECTIONS: Record<string, () => ReactNode> = {
  users: () => <AdminUsers />,
  policies: () => <AdminPolicies />,
  tenants: () => <AdminTenants />,
  audit: () => <AdminGovernance />,
  "master-data": () => <AdminMasterData />,
  config: () => <AdminConfig />,
};

export function screenFor(fullPath: string): (() => ReactNode) | undefined {
  // The notifications inbox is the same screen under every portal base (/reception/notifications,
  // /clinician/notifications, …) — map them all to one component rather than enumerating each.
  if (fullPath.endsWith("/notifications")) return () => <Notifications />;
  const admin = fullPath.match(/^\/(?:admin|platform)\/([a-z-]+)$/);
  if (admin && ADMIN_SECTIONS[admin[1]]) return ADMIN_SECTIONS[admin[1]];
  return SCREENS[fullPath];
}
