import type {
  AccessReviewCampaign,
  AppointmentRow,
  BeneficiaryRow,
  RegisterBeneficiaryInput,
  RegisterResult,
  StatusChangeResult,
  ApprovalItem,
  ApprovalReview,
  Beneficiary360,
  BreakGlassGrant,
  CheckInResult,
  DrugRef,
  EmergencyResult,
  ManualAuthInput,
  ManualAuthResult,
  MasterDataVersion,
  ProviderSummary,
  ProviderLocation,
  ProviderContract,
  CreateProviderInput,
  ReportView,
  SystemConfigEntry,
  TatSummary,
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
  OrderRow,
  ResultDetail,
  ReportAccessInput,
  ReportAccessRequestResult,
  ClaimRow,
  ReconciliationRow,
  ClaimsKpis,
  ResultTask,
  ResultUpload,
  PatientListItem,
  RxRow,
  RoleBinding,
  VitalInput,
  VitalsResult,
  SodConflict,
  TenantSummary,
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

  // Reception — day board (Phase 3). `filter` scopes the board: all / booked (arrivals to process) /
  // checked-in (waiting). checkIn transitions Booked → CheckedIn and enqueues a walk-in ticket.
  appointments(filter?: "all" | "booked" | "checked-in"): Promise<AppointmentRow[]>;
  checkIn(appointmentId: string): Promise<CheckInResult>;

  // Doctor — EMR (Phase 4)
  listPatients(): Promise<PatientListItem[]>;
  getEncounter(patientId: string): Promise<Encounter>;
  placeOrder(req: PlaceOrderRequest): Promise<PlaceOrderResult>;
  prescribe(req: PrescribeRequest): Promise<PrescribeResult>;
  /** The clinician's own orders (US-032). Pass status="Completed" for the results inbox. */
  ordersMine(status?: string): Promise<OrderRow[]>;
  /** The clinician's own e-prescriptions (US-033). */
  prescriptionsMine(status?: string): Promise<RxRow[]>;
  /**
   * Read a single completed result (14.6). Returns full values, OR existence-only metadata when the result is
   * sensitivity-restricted and the caller neither authored it nor holds an active grant (14.7 server gate).
   */
  resultDetail(orderId: string, lineId: string): Promise<ResultDetail>;
  /** Request time-boxed access to a restricted result (14.8) — purpose + justification are mandatory. */
  requestReportAccess(input: ReportAccessInput): Promise<ReportAccessRequestResult>;
  /** Record vitals on an encounter (nurse triage, US-030) — treating-gated server-side. */
  recordVitals(encounterId: string, readings: VitalInput[]): Promise<VitalsResult>;

  // Lab / imaging — queue + consume (Phase 5)
  labQueue(kind: "lab" | "imaging"): Promise<LabOrder[]>;
  consume(req: ConsumeRequest): Promise<ConsumeResult>;
  /** Consumed lines this provider still owes a result on (US-042). */
  awaitingResult(kind: "lab" | "imaging"): Promise<ResultTask[]>;
  /** Attach a result value to a consumed line (US-042). */
  uploadResult(orderId: string, lineId: string, resultValue: string): Promise<ResultUpload>;

  // Pharmacy — dispense (Phase 6)
  pharmacyQueue(): Promise<Prescription[]>;
  dispense(req: DispenseRequest): Promise<DispenseResult>;
  /** Formulary lookup for substitutions (US-052): search drugs, then list a drug's approved alternatives. */
  searchDrugs(query: string): Promise<DrugRef[]>;
  drugAlternatives(drugId: string): Promise<DrugRef[]>;

  // Approvals — worklist + decision (Phase 7)
  approvalWorklist(): Promise<ApprovalItem[]>;
  approvalReview(approvalId: string): Promise<ApprovalReview>;
  decide(req: DecisionRequest): Promise<DecisionResult>;
  // Approvals — break-glass + SLA (Phase 7.3)
  slaSummary(): Promise<TatSummary>;
  createManualAuth(input: ManualAuthInput): Promise<ManualAuthResult>;
  emergencyApprove(authId: string, justification: string): Promise<EmergencyResult>;

  // Executive dashboard (Phase 8)
  executiveDashboard(scope: "executive" | "finance" | "director"): Promise<ExecutiveDashboard>;
  // Director oversight / quality / escalations — de-identified reporting aggregates (Phase 8.3).
  directorReport(section: "oversight" | "quality" | "escalations"): Promise<ReportView>;

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

  // Claims management — codes + amounts only, no diagnosis (Phase 10b). Provider users isolated to own claims server-side.
  claimsWorklist(status?: string): Promise<ClaimRow[]>;
  claimsReconciliation(bucket?: string): Promise<ReconciliationRow[]>;
  claimsKpis(): Promise<ClaimsKpis>;

  // Notifications — the caller's own in-app inbox (Phase 8.1). Self-service, cross-portal.
  notifications(unreadOnly?: boolean): Promise<Notification[]>;
  markNotificationRead(id: string): Promise<MarkReadResult>;

  // Admin / platform governance (Phase 8b) — WHO can access, not content. Admin-role gated on the server.
  accessMatrix(): Promise<RoleBinding[]>;
  adminTenants(): Promise<TenantSummary[]>;
  sodMatrix(): Promise<SodConflict[]>;
  accessReviewCampaigns(): Promise<AccessReviewCampaign[]>;
  breakGlassGrants(): Promise<BreakGlassGrant[]>;
  adminMasterData(): Promise<MasterDataVersion[]>;
  adminSystemConfig(): Promise<SystemConfigEntry[]>;

  // Provider network — the tenant's provider directory (Phase 2b). Network-Team scope; no beneficiary PHI.
  providerList(): Promise<ProviderSummary[]>;
  providerLocations(providerId: string): Promise<ProviderLocation[]>;
  providerContracts(providerId: string): Promise<ProviderContract[]>;
  createProvider(input: CreateProviderInput): Promise<ProviderSummary>;

  // Beneficiary management — the beneficiary registry (Phase 1). Min-necessary identity, no clinical data.
  beneficiarySearch(query: { name?: string; status?: string }): Promise<BeneficiaryRow[]>;
  registerBeneficiary(input: RegisterBeneficiaryInput): Promise<RegisterResult>;
  changeBeneficiaryStatus(id: string, toStatus: string, reason: string): Promise<StatusChangeResult>;
}

/**
 * Fault injection for the dev/test client. `latencyMs` drives the loading state; `fault` lets a test render a
 * screen straight into its empty or error branch without a live backend.
 */
export interface ApiScenario {
  latencyMs?: number;
  fault?: "none" | "error" | "empty";
}
