import type {
  ApprovalItem,
  ApprovalReview,
  Beneficiary360,
  CaseListItem,
  ConsumeRequest,
  ConsumeResult,
  CoordinationTask,
  DecisionRequest,
  DecisionResult,
  DispenseRequest,
  DispenseResult,
  EligibilityHit,
  EligibilityResult,
  Encounter,
  Escalation,
  ExecutiveDashboard,
  ExportRequest,
  ExportResult,
  FinancialSummary,
  LabOrder,
  MarkReadResult,
  Notification,
  PatientListItem,
  PlaceOrderRequest,
  PlaceOrderResult,
  PrescribeRequest,
  PrescribeResult,
  Prescription,
  Settlement,
  UtilizationView,
} from "@mersal/contracts";

/**
 * The typed API surface every flagship screen consumes. Each method returns a zod-validated contract type.
 * `AuthClient`-style: this is an INTERFACE so the dev fixture client and the real HTTP client are swappable,
 * and tests can inject a client that simulates loading / empty / error / replay.
 *
 * Min-necessary is honoured by the CONTRACT TYPES themselves (masked refs, no cross-zone fields), so a screen
 * physically cannot read data outside its zone from this surface.
 */
export interface ApiClient {
  // Reception — eligibility (Phase 2)
  searchEligibility(query: string): Promise<EligibilityHit[]>;
  checkEligibility(beneficiaryId: string): Promise<EligibilityResult>;

  // Doctor — EMR (Phase 4)
  listPatients(): Promise<PatientListItem[]>;
  getEncounter(patientId: string): Promise<Encounter>;
  placeOrder(req: PlaceOrderRequest): Promise<PlaceOrderResult>;
  prescribe(req: PrescribeRequest): Promise<PrescribeResult>;

  // Lab / imaging — queue + consume (Phase 5)
  labQueue(kind: "lab" | "imaging"): Promise<LabOrder[]>;
  consume(req: ConsumeRequest): Promise<ConsumeResult>;

  // Pharmacy — dispense (Phase 6)
  pharmacyQueue(): Promise<Prescription[]>;
  dispense(req: DispenseRequest): Promise<DispenseResult>;

  // Approvals — worklist + decision (Phase 7)
  approvalWorklist(): Promise<ApprovalItem[]>;
  approvalReview(approvalId: string): Promise<ApprovalReview>;
  decide(req: DecisionRequest): Promise<DecisionResult>;

  // Executive dashboard (Phase 8)
  executiveDashboard(scope: "executive" | "finance" | "director"): Promise<ExecutiveDashboard>;

  // Case management — assignment-scoped (Phase 10.1). 360 is a coordination SUMMARY.
  myCases(): Promise<CaseListItem[]>;
  beneficiary360(caseId: string): Promise<Beneficiary360>;
  caseTasks(caseId: string): Promise<CoordinationTask[]>;
  escalations(): Promise<Escalation[]>;

  // Finance — billing codes + amounts only, no diagnosis (Phase 10.2).
  utilization(): Promise<UtilizationView>;
  settlements(): Promise<Settlement[]>;
  financialSummary(dimension: "serviceline" | "category" | "provider"): Promise<FinancialSummary>;
  exportReport(req: ExportRequest): Promise<ExportResult>;

  // Notifications — the caller's own in-app inbox (Phase 8.1). Self-service, cross-portal.
  notifications(unreadOnly?: boolean): Promise<Notification[]>;
  markNotificationRead(id: string): Promise<MarkReadResult>;
}

/**
 * Fault injection for the dev/test client. `latencyMs` drives the loading state; `fault` lets a test render a
 * screen straight into its empty or error branch without a live backend.
 */
export interface ApiScenario {
  latencyMs?: number;
  fault?: "none" | "error" | "empty";
}
