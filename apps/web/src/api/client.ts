import type {
  ApprovalItem,
  ApprovalReview,
  ConsumeRequest,
  ConsumeResult,
  DecisionRequest,
  DecisionResult,
  DispenseRequest,
  DispenseResult,
  EligibilityHit,
  EligibilityResult,
  Encounter,
  ExecutiveDashboard,
  LabOrder,
  PatientListItem,
  PlaceOrderRequest,
  PlaceOrderResult,
  PrescribeRequest,
  PrescribeResult,
  Prescription,
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
}

/**
 * Fault injection for the dev/test client. `latencyMs` drives the loading state; `fault` lets a test render a
 * screen straight into its empty or error branch without a live backend.
 */
export interface ApiScenario {
  latencyMs?: number;
  fault?: "none" | "error" | "empty";
}
