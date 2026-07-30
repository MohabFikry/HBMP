import type {
  AccessReviewCampaign,
  AccessSession,
  BranchScopeGrant,
  EffectiveAccess,
  MembershipDetail,
  MembershipRow,
  ProgramEnablement,
  AppointmentRow,
  BookableClinic,
  DoctorAvailability,
  AppointmentDay,
  AppointmentCounts,
  TimelineStep,
  BookableSlot,
  BookingRequest,
  BookingResult,
  BeneficiaryRow,
  RegisterBeneficiaryInput,
  RegisterResult,
  RegistrationDecisionResult,
  RegistrationWorkItem,
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
  Specialty,
  BranchSummary,
  Practitioner,
  CreatePractitionerInput,
  PractitionerCreated,
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
  MarkAllReadResult,
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
  IdentityUser,
  RoleScopeGrant,
  ReportAccessRequestRow,
  PatientProfile,
  ProfileSectionKey,
  ProfileExportSummary,
  CopySummariesResult,
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
  /**
   * The day board. `range` narrows to an inclusive span of Cairo civil days (the desk's custom date filter);
   * omitted, the server answers for today, which is what every existing caller wants.
   */
  appointments(
    filter?: "all" | "booked" | "checked-in",
    mine?: boolean,
    range?: { from: string; to: string },
    /** Cross-branch callers only (the call centre). A branch-scoped desk's own branch is server-resolved. */
    branchId?: string,
  ): Promise<AppointmentRow[]>;
  /** `rowVersion` (opt-in): the value read on the board, echoed as `If-Match` so a stale check-in loses to a
   * concurrent transition with 412 instead of double-acting. Omit to check in without the guard. */
  checkIn(appointmentId: string, rowVersion?: number): Promise<CheckInResult>;
  /** Mark a booked appointment as a no-show (US-022). Guarded server-side by the grace period, so call this
   * only for a row whose `noShowEligible` is true; `rowVersion` is echoed as `If-Match`. */
  noShow(appointmentId: string, rowVersion?: number): Promise<CheckInResult>;
  /**
   * Cancel an appointment, releasing its slot and promoting any waitlist entry behind it.
   *
   * A REASON is required — not by the schema but by us. A cancellation with no reason is unanswerable when
   * the patient rings back asking why, and it is the field every no-show/rebook report is grouped by.
   * `rowVersion` is echoed as `If-Match`, so cancelling a row someone else has already moved loses with 412
   * rather than silently cancelling a different state than the one on screen.
   */
  cancelAppointment(appointmentId: string, reason: string, rowVersion?: number): Promise<CheckInResult>;
  /**
   * Amend the general/administrative booking note. Captured in the appointment's timeline as a `NoteEdited`
   * step, which is the reason an edit is allowed at all rather than forcing a cancel-and-rebook.
   */
  updateAppointmentNote(appointmentId: string, note: string): Promise<void>;
  /** Move an appointment onto a different slot. Atomic release-and-hold; a taken slot answers 409. */
  rescheduleAppointment(appointmentId: string, newSlotId: string, rowVersion?: number): Promise<void>;
  /** How this appointment reached its current status — booked, checked in, no-showed, cancelled — with who and
   * when. Operational history from emr, NOT the audit store (which needs audit:read). */
  appointmentTimeline(appointmentId: string): Promise<TimelineStep[]>;
  /** Start the visit for a checked-in appointment (CheckedIn → an open encounter). Server-gated: the caller
   * must be the assigned practitioner, or the appointment must name none. Returns the encounter id. */
  startVisit(appointmentId: string, beneficiaryId: string): Promise<{ encounterId: string }>;

  /** The clinics the caller may book into, in their active branch (or `branchId` for cross-branch callers).
   * Derived from bookable SLOTS, so a clinic with no availability never appears — and reception never needs
   * `provider:read`, which it is correctly refused. */
  bookableClinics(branchId?: string): Promise<BookableClinic[]>;
  /** Open slots for a clinic session, for the desk's booking screen. The SERVER marks `open` — it holds the
   * no-double-book invariant and knows about slots held by bookings the desk cannot see. */
  openSlots(providerId: string, locationId: string, from?: string, to?: string, doctorId?: string): Promise<BookableSlot[]>;
  /**
   * Per-day open-slot counts for the booking calendar. Counted server-side: painting thirty cells must not
   * cost thousands of slot rows, and the Cairo day boundary is the server's to decide (see `zAppointmentDay`).
   */
  appointmentDays(providerId: string, locationId: string, from: string, to: string, doctorId?: string): Promise<AppointmentDay[]>;
  /** Book a slot. A branch-scoped desk omits `branchId` — the server stamps its active branch and refuses a
   * request naming a different one. Returns 409 (surfaced as ApiError) when the slot was taken concurrently. */
  bookAppointment(input: BookingRequest): Promise<BookingResult>;

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
  /**
   * 18.C2 (W4) — the approver inbox. Without a list endpoint AND a screen, a request could be raised and
   * decided by id but never DISCOVERED, so the sensitive-result gate was permanent-deny in practice.
   */
  reportAccessInbox(): Promise<ReportAccessRequestRow[]>;
  /** Approve / deny / ask for more information. `ttlHours` is capped server-side by the result's sensitivity. */
  decideReportAccess(requestId: string, decision: "approve" | "deny" | "requestinfo", reason: string, ttlHours?: number): Promise<void>;
  /** Revoke a live grant early (the request follows it to Revoked). */
  revokeReportAccessGrant(grantId: string): Promise<void>;
  /** Record vitals on an encounter (nurse triage, US-030) — treating-gated server-side. */
  recordVitals(encounterId: string, readings: VitalInput[]): Promise<VitalsResult>;

  // Lab / imaging — queue + consume (Phase 5)
  labQueue(kind: "lab" | "imaging"): Promise<LabOrder[]>;
  consume(req: ConsumeRequest): Promise<ConsumeResult>;
  /** Consumed lines this provider still owes a result on (US-042). */
  awaitingResult(kind: "lab" | "imaging"): Promise<ResultTask[]>;
  /** Attach a result value to a consumed line (US-042). */
  uploadResult(orderId: string, lineId: string, resultValue: string, idempotencyKey?: string): Promise<ResultUpload>;

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
  createManualAuth(input: ManualAuthInput, idempotencyKey?: string): Promise<ManualAuthResult>;
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
  /** Clears the caller's whole unread inbox in one call; resolves with how many rows it marked. */
  markAllNotificationsRead(): Promise<MarkAllReadResult>;

  // Admin / platform governance (Phase 8b) — WHO can access, not content. Admin-role gated on the server.
  /** 18.C2 (W5) — users from the IDENTITY STORE (active + 2FA state), not the admin-service projection. */
  identityUsers(query?: string): Promise<IdentityUser[]>;
  /** 18.C2 (W5) — the live role→scope matrix the token issuer actually reads. */
  identityRoleScopes(): Promise<RoleScopeGrant[]>;
  accessMatrix(): Promise<RoleBinding[]>;
  adminTenants(): Promise<TenantSummary[]>;
  sodMatrix(): Promise<SodConflict[]>;
  accessReviewCampaigns(): Promise<AccessReviewCampaign[]>;
  breakGlassGrants(): Promise<BreakGlassGrant[]>;
  adminMasterData(): Promise<MasterDataVersion[]>;
  adminSystemConfig(): Promise<SystemConfigEntry[]>;

  // User & access model (Phase 21.6, design 40) — the MEMBERSHIP is the principal, never the identity.
  /** The tenant's membership roster. Server-side tenant-pinned: asking for another tenant is 403 + audited. */
  memberships(tenant?: string, status?: string, query?: string): Promise<MembershipRow[]>;
  membership(membershipId: string): Promise<MembershipDetail>;
  /**
   * Set or replace one per-membership override — the SoD-guarded exception path (§2).
   *
   * A reason is part of the signature, not an option: the server refuses without one, and an exception
   * nobody explained cannot be judged at review time. An Allow that would create a forbidden combination
   * comes back 409 with both halves of the duty named.
   */
  setMembershipOverride(
    membershipId: string,
    input: { scopeKey: string; effect: "Allow" | "Deny"; reason: string; validUntil: string | null },
  ): Promise<void>;
  /**
   * Mode-2 effective access — "what can this person actually do, and why".
   *
   * Recomputed server-side from the store. The browser never reduces the set algebra itself: a third
   * implementation would be a third opinion about who can do what, and the parity suite only covers the two
   * on the server (§5).
   */
  effectiveAccess(membershipId: string): Promise<EffectiveAccess>;
  /** Reach, from admin-service — branch grants are a different service from the authority above (§3). */
  branchScopeGrants(subject: string, tenant?: string): Promise<BranchScopeGrant[]>;
  /** Sessions for a membership's underlying identity, and the revoke behind the sessions tab. */
  accessSessions(userId: string): Promise<AccessSession[]>;
  revokeAccessSession(userId: string, sessionId: string): Promise<void>;
  /** Programme enablement (§4). Reads are open to org admins; the writes below are platform-admin only. */
  programEnablement(tenant: string): Promise<ProgramEnablement>;
  setProgramFeature(tenant: string, key: string, enabled: boolean, reason: string): Promise<void>;
  setProgramLimit(tenant: string, key: string, maxValue: number, reason: string): Promise<void>;

  // Provider network — the tenant's provider directory (Phase 2b). Network-Team scope; no beneficiary PHI.
  providerList(): Promise<ProviderSummary[]>;
  providerLocations(providerId: string): Promise<ProviderLocation[]>;
  providerContracts(providerId: string): Promise<ProviderContract[]>;
  createProvider(input: CreateProviderInput, idempotencyKey?: string): Promise<ProviderSummary>;

  // Practitioners (Phase 14.5, design 37 §4) — the clinical profile behind a user, with the specialty and
  // the clinics that the booking screen filters on.
  /** Reference specialties (org data). */
  specialties(): Promise<Specialty[]>;
  /** The Mersal internal branches. Org reference data — readable by any authenticated caller. */
  branches(): Promise<BranchSummary[]>;
  /** The practitioner list, optionally narrowed the same way the booking picker narrows it. */
  practitioners(filter?: { branchId?: string; specialtyCode?: string; type?: string }): Promise<Practitioner[]>;
  /**
   * Create a doctor: the practitioner row, its primary specialty and one assignment per clinic.
   *
   * Resolves with `incomplete` NON-EMPTY when the practitioner was created but an attachment failed — see
   * `zPractitionerCreated`. It rejects only when the practitioner row itself could not be created, because
   * that is the only failure after which nothing exists and a retry is safe.
   */
  createPractitioner(input: CreatePractitionerInput, idempotencyKey?: string): Promise<PractitionerCreated>;

  // Amending an existing clinician. Each is one server call, so each can fail on its own and the panel
  // reports them one at a time — unlike creation, there is no multi-step partial state to reconcile.
  /** Add a specialty the practitioner does not yet hold (never primary — use `setPrimarySpecialty`). */
  assignSpecialty(practitionerId: string, specialtyCode: string): Promise<void>;
  /** Promote a specialty to primary, clearing the previous one. Assigns it first if not already held. */
  setPrimarySpecialty(practitionerId: string, specialtyCode: string): Promise<void>;
  /** Remove a non-primary specialty. The server refuses (409) if it is the primary one. */
  revokeSpecialty(practitionerId: string, specialtyCode: string): Promise<void>;
  assignPractitionerBranch(practitionerId: string, branchId: string): Promise<void>;
  /** End an assignment (status → Revoked). Makes `serves-branch` false, so new bookings there are refused. */
  revokePractitionerBranch(practitionerId: string, branchId: string): Promise<void>;
  setPractitionerStatus(practitionerId: string, status: string, reason: string): Promise<void>;

  /**
   * Which DOCTORS have open time, from emr — ids and counts only, no names (that is provider-service's to
   * disclose, and this app reads it there under `practitioner:read`). Join the two with `bookableDoctors`.
   * A branch-scoped desk omits `branchId`; the server uses its active branch and refuses another.
   */
  doctorAvailability(branchId?: string): Promise<DoctorAvailability[]>;

  /** Total / checked-in / no-show for one Cairo day, counted server-side and branch-scoped like the board. */
  appointmentCounts(date?: string): Promise<AppointmentCounts>;

  // Patient profile (Phase 20, design 39) — ONE endpoint, projected server-side to the caller's role.
  /**
   * Open a patient profile. `sections` narrows the request (the context bar asks for header+alerts only); it
   * can never widen it — the server's matrix decides regardless of what was asked for.
   *
   * The response carries only what this role may see: a withheld section arrives with no `data` property at
   * all. Screens render whatever came back and contain NO role logic of their own.
   */
  patientProfile(beneficiaryId: string, sections?: ProfileSectionKey[]): Promise<PatientProfile>;
  /**
   * "Copy all visible" call summaries. Returns the SERVER-GENERATED block and writes one `CallSummaryCopied`
   * audit event — copying is when PHI leaves the platform's control, so it is logged like an export.
   */
  copyCallSummaries(beneficiaryId: string, callRefs: string[]): Promise<CopySummariesResult>;
  /**
   * The role-projected print summary, composed SERVER-SIDE from the same projection and audited as a PHI
   * export. Never rendered from the DOM — that would make the export's contents a property of what this
   * browser happened to have loaded, and would skip the export audit entirely.
   */
  profileSummary(beneficiaryId: string): Promise<ProfileExportSummary>;

  // Beneficiary management — the beneficiary registry (Phase 1). Min-necessary identity, no clinical data.
  beneficiarySearch(query: { name?: string; status?: string }): Promise<BeneficiaryRow[]>;
  registerBeneficiary(input: RegisterBeneficiaryInput, idempotencyKey?: string): Promise<RegisterResult>;
  changeBeneficiaryStatus(id: string, toStatus: string, reason: string): Promise<StatusChangeResult>;
  /** The approver's queue (US-003): Pending beneficiaries + their latest application. Oldest first. */
  registrationWorklist(): Promise<RegistrationWorkItem[]>;
  /** Open an application for a beneficiary that has none (legacy rows) or whose last one was Rejected. */
  createRegistration(beneficiaryId: string, idempotencyKey?: string): Promise<void>;
  /** The officer's preparation step — the two approval guards the server checks before Approve. */
  setRegistrationChecks(id: string, checks: { documentsVerified?: boolean; coverageBound?: boolean }): Promise<void>;
  /**
   * The approver's decision. Supervisor-only ON THE SERVER (urn:hbmp:approver-required) — the officer who
   * vouched for the documents must not be the one who activates. Approve returns the issued member number.
   */
  decideRegistration(id: string, decision: "Approve" | "RequestInfo" | "Reject", notes?: string): Promise<RegistrationDecisionResult>;
}

/**
 * Fault injection for the dev/test client. `latencyMs` drives the loading state; `fault` lets a test render a
 * screen straight into its empty or error branch without a live backend.
 */
export interface ApiScenario {
  latencyMs?: number;
  fault?: "none" | "error" | "empty";
}
