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
const ReceptionDashboard = lazy(() => import("./ReceptionDashboard").then((m) => ({ default: m.ReceptionDashboard })));
const ReceptionAppointments = lazy(() => import("./ReceptionDesk").then((m) => ({ default: m.ReceptionAppointments })));
const ReceptionBooking = lazy(() => import("./ReceptionBooking").then((m) => ({ default: m.ReceptionBooking })));
const DoctorVisits = lazy(() => import("./DoctorVisits").then((m) => ({ default: m.DoctorVisits })));
// Beneficiary-management portal (Phase 1) — register / manage / status share one chunk.
const BeneficiaryRegister = lazy(() => import("./BeneficiaryPortal").then((m) => ({ default: m.BeneficiaryRegister })));
// US-003 — the approval worklist, shared by the officer (prepares) and the supervisor (decides).
const RegistrationApprovals = lazy(() => import("./BeneficiaryPortal").then((m) => ({ default: m.RegistrationApprovals })));
const DoctorEncounter = lazy(() => import("./DoctorEncounter").then((m) => ({ default: m.DoctorEncounter })));
// 18.C2 (W4) — the sensitive-result approver inbox, shared by the Doctor and Medical Director portals.
const ReportAccessInbox = lazy(() => import("./ReportAccessInbox").then((m) => ({ default: m.ReportAccessInbox })));
const ValidityPolicyAdmin = lazy(() => import("./ValidityPolicyAdmin").then((m) => ({ default: m.ValidityPolicyAdmin })));
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
const PrescriptionPage = lazy(() => import("./pharmacy/PrescriptionPage").then((m) => ({ default: m.PrescriptionPage })));
// ADR-0034 — the bench's counterpart of the prescription page. Its own chunk: the queue is opened many times
// a day and this is opened once per patient, so folding them together would make the common case heavier.
const InvestigationOrderPage = lazy(() => import("./lab/InvestigationOrderPage").then((m) => ({ default: m.InvestigationOrderPage })));
// Pharmacy substitutions (Phase 6.3) — formulary lookup of policy-approved alternatives.
const Substitutions = lazy(() => import("./Substitutions").then((m) => ({ default: m.Substitutions })));
const ApprovalsWorklist = lazy(() => import("./ApprovalsWorklist").then((m) => ({ default: m.ApprovalsWorklist })));
// ADR-0034 — the register of every authorization, including what was actually delivered at counters/benches.
const ApprovalsRegister = lazy(() => import("./ApprovalsRegister").then((m) => ({ default: m.ApprovalsRegister })));
// Approvals break-glass + SLA (Phase 7.3) — manual auth / emergency approve / TAT board share one chunk.
const ApprovalsManual = lazy(() => import("./ApprovalsExtra").then((m) => ({ default: m.ApprovalsManual })));
const ApprovalsEmergency = lazy(() => import("./ApprovalsExtra").then((m) => ({ default: m.ApprovalsEmergency })));
const ApprovalsSla = lazy(() => import("./ApprovalsExtra").then((m) => ({ default: m.ApprovalsSla })));
const ExecutiveDashboard = lazy(() => import("./ExecutiveDashboard").then((m) => ({ default: m.ExecutiveDashboard })));
// Director oversight / quality / escalations (Phase 8.3) — one generic report screen, parameterised.
const DirectorReport = lazy(() => import("./ReportView").then((m) => ({ default: m.DirectorReport })));
// Provider network portal (Phase 2b) — directory / performance / contracts / locations / onboarding share one chunk.
const NetworkDirectory = lazy(() => import("./NetworkPortal").then((m) => ({ default: m.NetworkDirectory })));
const NetworkPerformance = lazy(() => import("./NetworkPortal").then((m) => ({ default: m.NetworkPerformance })));
const NetworkContracts = lazy(() => import("./NetworkPortal").then((m) => ({ default: m.NetworkContracts })));
const NetworkLocations = lazy(() => import("./NetworkPortal").then((m) => ({ default: m.NetworkLocations })));
const NetworkOnboarding = lazy(() => import("./NetworkPortal").then((m) => ({ default: m.NetworkOnboarding })));
// 14.5 — practitioners. Its own chunk rather than folding into NetworkPortal: it pulls the identity account
// list and the specialty/branch reference data, none of which the other five network screens need.
const NetworkPractitioners = lazy(() => import("./PractitionerAdmin").then((m) => ({ default: m.PractitionerAdmin })));
// Case-manager portal (Phase 10.3) — one chunk for the two case screens.
const MyCases = lazy(() => import("./CaseManager").then((m) => ({ default: m.MyCases })));
const Escalations = lazy(() => import("./CaseManager").then((m) => ({ default: m.Escalations })));
// Finance portal (Phase 10.3) — one chunk for the four finance screens.
const FinanceUtilization = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceUtilization })));
const FinanceSettlements = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceSettlements })));
const FinanceSummaries = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceSummaries })));
const FinanceExports = lazy(() => import("./FinancePortal").then((m) => ({ default: m.FinanceExports })));
// Call Centre portal (Phase 15.5) — the call-shaped workspace + call history share one chunk.
const CallCentreAppointments = lazy(() => import("./CallCentreAppointments").then((m) => ({ default: m.CallCentreAppointments })));
const CallCentreBooking = lazy(() => import("./CallCentreBooking").then((m) => ({ default: m.CallCentreBooking })));
const CallCentreWorkspace = lazy(() => import("./CallCentre").then((m) => ({ default: m.CallCentreWorkspace })));
const CallHistory = lazy(() => import("./CallCentre").then((m) => ({ default: m.CallHistory })));
// Claims portal (Phase 10b) — worklist / reconciliation / insights share one chunk. Codes + amounts only, no diagnosis.
const ClaimsWorklist = lazy(() => import("./ClaimsPortal").then((m) => ({ default: m.ClaimsWorklist })));
const ClaimsReconciliation = lazy(() => import("./ClaimsPortal").then((m) => ({ default: m.ClaimsReconciliation })));
const ClaimsInsights = lazy(() => import("./ClaimsPortal").then((m) => ({ default: m.ClaimsInsights })));
// Cross-cutting inbox (Phase 8.1) — one chunk, mounted under every portal's `/…/notifications` route.
const Notifications = lazy(() => import("./Notifications").then((m) => ({ default: m.Notifications })));
// Admin / platform governance (Phase 8b) — one chunk, mounted under both the org-admin (/admin) and
// super-admin (/platform) portal bases which share section paths.
const AdminUsers = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminUsers })));
const AdminPolicies = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminPolicies })));
const AdminTenants = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminTenants })));
const AdminGovernance = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminGovernance })));
const ApprovalEngineAdmin = lazy(() => import("./ApprovalEngineAdmin").then((m) => ({ default: m.ApprovalEngineAdmin })));
const DocumentValidityAdmin = lazy(() => import("./DocumentValidityAdmin").then((m) => ({ default: m.DocumentValidityAdmin })));
const MasterListAdmin = lazy(() => import("./MasterListAdmin").then((m) => ({ default: m.MasterListAdmin })));
const AdminMasterData = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminMasterData })));
const AdminConfig = lazy(() => import("./AdminConsole").then((m) => ({ default: m.AdminConfig })));
// Policy administration (Phase 19.6) — four chunks rather than one, because the portal's sections are used by
// two different roles: a beneficiary-management officer opens Members and Bulk and never touches the plan
// version editor, which is the heaviest screen here.
const PolicyPayers = lazy(() => import("./PolicyProductAdmin").then((m) => ({ default: m.PolicyPayers })));
const PolicyPlans = lazy(() => import("./PolicyProductAdmin").then((m) => ({ default: m.PolicyPlans })));
const PolicyList = lazy(() => import("./PolicyBook").then((m) => ({ default: m.PolicyList })));
const GroupsScreen = lazy(() => import("./PolicyBook").then((m) => ({ default: m.GroupsScreen })));
const UtilizationScreen = lazy(() => import("./PolicyBook").then((m) => ({ default: m.UtilizationScreen })));
const MemberSearch = lazy(() => import("./MemberAdmin").then((m) => ({ default: m.MemberSearch })));
const BulkJobs = lazy(() => import("./PolicyBulk").then((m) => ({ default: m.BulkJobs })));
const NetworkTiers = lazy(() => import("./NetworkTierAdmin").then((m) => ({ default: m.NetworkTiers })));
// Unified patient profile (Phase 20) — ONE screen for every role. It code-splits into its own chunk because
// almost every portal links into it, and duplicating it per portal would duplicate the one component whose
// whole design is that there is exactly one of it.
const PatientProfile = lazy(() => import("./PatientProfile").then((m) => ({ default: m.PatientProfile })));
// 19.6b — the analytical dashboard. Its own chunk: it is the heaviest screen in the portal and three of the
// four roles that can reach it open it rarely.
const PolicyAnalytics = lazy(() => import("./PolicyAnalytics").then((m) => ({ default: m.PolicyAnalytics })));
// User & access model (Phase 21.6, design 40) — the membership roster and its detail tabs share one chunk;
// programme enablement is a separate chunk because only platform administration ever opens it.
const MembershipRoster = lazy(() => import("./AccessAdmin").then((m) => ({ default: m.MembershipRoster })));
const ProgramAdmin = lazy(() => import("./ProgramAdmin").then((m) => ({ default: m.ProgramAdmin })));

// 25.7 — Branch Management (design 42 §6). ONE portal, two roles: every path below is mounted once and
// serves both, because the difference between a coordinator and a clinics manager is REACH, resolved
// server-side from the active-branch header, not a different screen.
const BranchPractitioners = lazy(() => import("./BranchLicences").then((m) => ({ default: m.BranchPractitioners })));
const BranchLicenceAlerts = lazy(() => import("./BranchLicences").then((m) => ({ default: m.BranchLicenceAlerts })));
const BranchRoster = lazy(() => import("./BranchRoster").then((m) => ({ default: m.BranchRoster })));
const BranchInventory = lazy(() => import("./BranchInventory").then((m) => ({ default: m.BranchInventory })));
const BranchesOverview = lazy(() => import("./BranchesOverview").then((m) => ({ default: m.BranchesOverview })));

export const SCREENS: Record<string, () => ReactNode> = {
  // 25.7 — Branch Management. The first four REUSE reception's screens rather than copying them: the desk
  // work a coordinator does is the same desk work, and a second implementation would be a second place for
  // the branch board to disagree with itself.
  "/branch/dashboard": () => <ReceptionDashboard />,
  "/branch/eligibility": () => <ReceptionEligibility />,
  "/branch/appointments": () => <ReceptionAppointments />,
  "/branch/book": () => <ReceptionBooking />,
  "/branch/practitioners": () => <BranchPractitioners />,
  "/branch/roster": () => <BranchRoster />,
  "/branch/licence-alerts": () => <BranchLicenceAlerts />,
  "/branch/inventory": () => <BranchInventory />,
  "/branch/branches": () => <BranchesOverview />,
  // 1. Reception — eligibility (also surfaced in the beneficiary-management portal).
  "/reception/eligibility": () => <ReceptionEligibility />,
  "/beneficiaries/eligibility": () => <ReceptionEligibility />,
  "/beneficiaries/register": () => <BeneficiaryRegister />,
  "/beneficiaries/approvals": () => <RegistrationApprovals />,
  // `/beneficiaries/manage`, `/beneficiaries/status` and `/beneficiaries/utilization` were RETIRED with
  // their nav sections. They are removed from here too, not just from the catalog: a path with no section
  // falls through to the deep-link branch of AppRouter, which resolves it from this map — so leaving them
  // would have kept three withdrawn screens reachable by typing their URL, gated only by `profile.read`.
  "/reception/dashboard": () => <ReceptionDashboard />,
  "/reception/appointments": () => <ReceptionAppointments />,
  "/reception/book": () => <ReceptionBooking />,
  // 2. Doctor — consultation / EMR + cross-encounter worklists.
  "/clinician/visits": () => <DoctorVisits />,
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
  // The /queue paths stay resolvable even though the rail no longer offers them: a bookmark or a link in
  // somebody's notes should land on the bench rather than on a 404. Same treatment /pharmacy/queue got.
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
  "/approvals/authorizations": () => <ApprovalsRegister />,
  "/approvals/manual": () => <ApprovalsManual />,
  "/approvals/emergency": () => <ApprovalsEmergency />,
  "/approvals/sla": () => <ApprovalsSla />,
  // 18.C2 (W4) — result-access approvals. Reachable from BOTH portals because 37 §6 routes a request to the
  // authoring doctor AND allows a Medical Director to decide when that doctor is unavailable — which is the
  // case the escalation path exists for, so it cannot live only under /clinician.
  "/clinician/result-access": () => <ReportAccessInbox />,
  "/director/result-access": () => <ReportAccessInbox />,
  "/director/validity": () => <ValidityPolicyAdmin />,
  // ADR-0035 §3. The same screen is NOT re-used from the admin portal — that one is read-only, and this is
  // the editor. `portalForRole` gives one portal per role, so a second door is how the director reaches an
  // authority they already held (`admin:edit-masterdata`) and had no route to.
  "/director/master-lists": () => <MasterListAdmin />,
  "/director/document-validity": () => <DocumentValidityAdmin />,
  "/director/engine": () => <ApprovalEngineAdmin />,
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
  // The same screen for all three roles. reporting-service gates the financial and network views on the
  // financial zone, so a beneficiary-management officer opening it sees four views and a 403 on two — the
  // server decides, not a second copy of the screen.
  "/finance/analytics": () => <PolicyAnalytics />,
  // 9. Provider network (Phase 2b) — Network-Team directory + onboarding + contracts/locations + performance.
  "/network/directory": () => <NetworkDirectory />,
  "/network/onboarding": () => <NetworkOnboarding />,
  "/network/contracts": () => <NetworkContracts />,
  "/network/locations": () => <NetworkLocations />,
  "/network/practitioners": () => <NetworkPractitioners />,
  "/network/performance": () => <NetworkPerformance />,
  // 10. Call Centre (Phase 15) — the call-shaped workspace + call history. No clinical route exists.
  "/call-centre/workspace": () => <CallCentreWorkspace />,
  "/call-centre/appointments": () => <CallCentreAppointments />,
  "/call-centre/book": () => <CallCentreBooking />,
  "/call-centre/history": () => <CallHistory />,
  // 11. Claims (Phase 10b) — worklist / reconciliation / insights. Codes + amounts only; no clinical route exists.
  "/claims/worklist": () => <ClaimsWorklist />,
  "/claims/reconciliation": () => <ClaimsReconciliation />,
  "/claims/insights": () => <ClaimsInsights />,
  // 12. Policy administration (Phase 19.6) — the benefit product and the policy book. No clinical route.
  "/policy/payers": () => <PolicyPayers />,
  "/policy/plans": () => <PolicyPlans />,
  "/policy/policies": () => <PolicyList />,
  "/policy/members": () => <MemberSearch />,
  "/policy/groups": () => <GroupsScreen />,
  "/policy/utilization": () => <UtilizationScreen />,
  "/policy/bulk": () => <BulkJobs />,
  "/policy/analytics": () => <PolicyAnalytics />,
  "/policy/tiers": () => <NetworkTiers />,
  // 13. The membership sections the beneficiary-management portal shares with policy administration. Same
  // screens, same server-side projection — a second implementation would be a second answer to "may this
  // officer see the money".
  "/beneficiaries/members": () => <MemberSearch />,
  "/beneficiaries/groups": () => <GroupsScreen />,
  "/beneficiaries/bulk": () => <BulkJobs />,
  "/beneficiaries/analytics": () => <PolicyAnalytics />,
  // 14. Network tiers under the Network Team's own portal (write) — the same screen policy admins read.
  "/network/tiers": () => <NetworkTiers />,
  // 15. The unified patient profile (Phase 20, design 39). Reachable from every portal that can open a
  // patient; the SERVER decides what each of them sees, so one route serves all of them.
  "/reception/patient": () => <PatientProfile />,
  "/clinician/patient": () => <PatientProfile />,
  "/nurse/patient": () => <PatientProfile />,
  "/approvals/patient": () => <PatientProfile />,
  "/cases/patient": () => <PatientProfile />,
  "/call-centre/patient": () => <PatientProfile />,
  "/beneficiaries/patient": () => <PatientProfile />,
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
  // 21.6 — the membership roster. Mounted under BOTH admin bases: an org admin administers their own
  // tenant's memberships, and the server pins the tenant either way (asking for another is 403 + audited).
  access: () => <MembershipRoster />,
  // Platform administration only. The catalog omits it from the org-admin portal, but that is cosmetic —
  // the write endpoints require the platform-admin role and refuse regardless of who reaches the route.
  programs: () => <ProgramAdmin />,
};

export function screenFor(fullPath: string): (() => ReactNode) | undefined {
  // The deep link design 39 §6 names: /patients/{beneficiaryId} resolves to the caller's own projection.
  // Unauthorized deep links are refused by the SERVICE with 403 + audit, never by hiding the route — a route
  // the SPA hides is a route the SPA can be persuaded to unhide.
  const patient = fullPath.match(/^\/patients\/([^/]+)$/);
  if (patient) return () => <PatientProfile beneficiaryId={decodeURIComponent(patient[1])} />;

  // One prescription, on its own page. It has a URL so a pharmacist who reloads — or who hands the screen to
  // a colleague mid-shift — lands back on the prescription they were dispensing rather than an empty search.
  // Keyed by the Rx NUMBER rather than the uuid: that is the reference printed on the paper in the patient's
  // hand, so the address bar and the prescription agree.
  const rx = fullPath.match(/^\/pharmacy\/rx\/([^/]+)$/);
  if (rx) return () => <PrescriptionPage rxNo={decodeURIComponent(rx[1])} />;

  // The bench's counterpart, on the same terms and keyed by the ORDER NUMBER for the same reason — that is
  // what is printed on the paper the patient handed over. Mounted under both /lab and /imaging because they
  // are the same screen for two capabilities, and a technician's portal base is not the order's business.
  const order = fullPath.match(/^\/(?:lab|imaging)\/order\/([^/]+)$/);
  if (order) return () => <InvestigationOrderPage orderNo={decodeURIComponent(order[1])} />;

  // The notifications inbox is the same screen under every portal base (/reception/notifications,
  // /clinician/notifications, …) — map them all to one component rather than enumerating each.
  if (fullPath.endsWith("/notifications")) return () => <Notifications />;
  const admin = fullPath.match(/^\/(?:admin|platform)\/([a-z-]+)$/);
  if (admin && ADMIN_SECTIONS[admin[1]]) return ADMIN_SECTIONS[admin[1]];
  return SCREENS[fullPath];
}
