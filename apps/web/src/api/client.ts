import type {
  ChronicPreview,
  QuantityPreview,
  OrderableService,
  ProcedureQueueItem,
  ProcedureType,
  ReferralCreated,
  PrescriptionKind,
  RefillFrequency,
  SessionProgress,
  ServiceHistory,
  RxPricing,
  AuthorizationItem,
  InvestigationOrder,
  OrderPricing,
  SubstitutionRequest,
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
  BeneficiaryDocument,
  BeneficiaryEdit,
  BulkDecisionOutcome,
  RegistrationDecisionResult,
  RegistrationThreadEntry,
  RegistrationWorklistPage,
  StatusChangeResult,
  ApprovalItem,
  ApprovalReview,
  Beneficiary360,
  BreakGlassGrant,
  CheckInResult,
  AllergenOption,
  AddAllergyRequest,
  AllergyRecord,
  BloodGroup,
  MemberClinicalRecord,
  DrugRef,
  PrescribableDrug,
  PrescriptionDraftLine,
  PrescriptionSubmitResult,
  LineAcknowledgement,
  ValidationResult,
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
  SystemConfigEdit,
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
  DiagnosisRank,
  EncounterDiagnosis,
  IcdRef,
  Soap,
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
  RoleCatalogEntry,
  RoleScopeGrant,
  ScopeCatalogEntry,
  ReportAccessRequestRow,
  PatientProfile,
  CptRef,
  CptSection,
  InvestigationDraftLine,
  InvestigationOrderResult,
  InvestigationOrderType,
  OrderAcknowledgement,
  OrderValidationResult,
  ValidityExtensionRequest,
  ValidityExtensionResult,
  ValidityPolicyView,
  ProfileSectionKey,
  ProfileExportSummary,
  CopySummariesResult,
  MasterDataEdit,
  MasterDataAsOf,
  DocumentValidityView,
  SetDocumentValidity,
  ApprovalRuleList,
  SaveApprovalRule,
  AutoDecisionSwitch,
  SetAutoDecision,
  AmendReasonOption,
  WithdrawResult,
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
  /**
   * ICD-10 titles for the codes on screen, from masterdata-service.
   *
   * <b>A client-side join, deliberately.</b> emr stores a diagnosis as a bare code and says so: resolving
   * "I10" to "Essential (primary) hypertension" is masterdata's job, and doing it in emr would make emr a
   * second place that answers what a code means. The browser holds the read for both, so it joins them —
   * the same shape as `bookableDoctors`, and no more privileged than either call on its own.
   *
   * Missing codes are simply absent from the map; the caller falls back to showing the code.
   */
  icdTitles(codes: readonly string[]): Promise<Map<string, string>>;
  /**
   * Branch id → display name, from the LABEL-ONLY lookup. Branch names proper live behind `provider:read`,
   * which the desks, the call centre and a clinician do not hold; `/branch-labels` exists so a row can be
   * named without handing out the provider directory. Missing ids are absent from the map.
   */
  branchLabels(branchIds: readonly string[]): Promise<Map<string, string>>;
  // Reception — eligibility (Phase 2)
  searchEligibility(query: string, signal?: AbortSignal): Promise<EligibilityHit[]>;
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
  /** The care episode of one VISIT (ADR-0031) — the encounter workspace's own history, and the source the
   *  order and prescription dialogs filter by reference to show what happened to one transaction. */
  encounterTimeline(encounterId: string): Promise<TimelineStep[]>;

  // ---- 30.6 amend / cancel a signed line (design 46 §1-§3) --------------------------------------------
  /** The CODED reason vocabulary for the picker. Served, not hard-coded, so adding one stays a data change. */
  amendmentReasons(kind: "order" | "prescription"): Promise<AmendReasonOption[]>;
  /** Withdraw one line. The reason code is mandatory; the free text is additional, never instead. */
  cancelOrderLine(orderId: string, lineId: string, reasonCode: string, reasonText?: string): Promise<void>;
  /** Supersede one line. The signed row is never edited — a new version replaces it. */
  amendOrderLine(
    orderId: string, lineId: string, quantityOrdered: number, reasonCode: string, reasonText?: string,
  ): Promise<void>;
  cancelPrescriptionLine(
    rxId: string, lineId: string, reasonCode: string, reasonText?: string,
  ): Promise<void>;
  amendPrescriptionLine(
    rxId: string, lineId: string, quantityPrescribed: number, reasonCode: string, reasonText?: string,
  ): Promise<void>;
  /**
   * Withdraw a WHOLE transaction — every line of it that can still be withdrawn.
   *
   * <p>Reached from the row rather than from inside the record, because "withdraw this prescription" is the
   * act a doctor actually intends; withdrawing four lines one at a time is that act performed four times,
   * with four chances to stop halfway. The result reports PARTIAL success plainly (design 46 §3) — a line
   * already dispensed cannot be withdrawn, and the doctor has to be told which.</p>
   */
  withdrawPrescription(rxId: string, reasonCode: string, reasonText?: string): Promise<WithdrawResult>;
  withdrawOrder(orderId: string, reasonCode: string, reasonText?: string): Promise<WithdrawResult>;
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
  /**
   * Write the encounter's working SOAP note. Pass the encounter's `noteId` to amend the note already there,
   * or null to open the first one — the two are different verbs server-side (POST vs PUT), and calling the
   * wrong one either fails or leaves a second partial note on the encounter. Returns the id to save with
   * next time.
   *
   * Refuses a note with nothing in any of S/O/A/P: emr treats an empty note as a 422, because a blank
   * clinical record that LOOKS documented is worse than an encounter openly still in progress.
   */
  saveEncounterNote(encounterId: string, noteId: string | null, soap: Soap): Promise<{ noteId: string }>;
  /**
   * Sign the note — the finalize half of "Save & finalize". Signing LOCKS it: from here corrections are
   * addenda, never edits, and only the note's author may sign.
   */
  signEncounterNote(encounterId: string, noteId: string): Promise<void>;
  /**
   * End the visit: the encounter closes and its appointment moves to Completed, in one server transaction.
   *
   * Signing the note is NOT the same act. A signed note is a finished piece of documentation; a closed visit
   * is a patient who has left the room, and it is what takes the appointment off the day list. Until this
   * existed, a finished consultation stayed CheckedIn and "Start visit" was still offered for it.
   */
  completeEncounter(encounterId: string): Promise<void>;
  /**
   * Record an ICD-10 diagnosis on the encounter. The code is validated against master data server-side.
   * The RANK is the doctor's call — which condition the visit was chiefly about is a clinical judgement,
   * not something derivable from the order the codes were entered in.
   */
  addEncounterDiagnosis(encounterId: string, icdCode: string, rank?: DiagnosisRank): Promise<EncounterDiagnosis>;
  /** Retract one — soft-deleted, and refused once the encounter's note is signed (409). */
  removeEncounterDiagnosis(encounterId: string, diagnosisId: string): Promise<void>;
  /** ICD-10 typeahead over master data. Empty query → no rows, never the whole catalogue. */
  searchIcd(query: string, signal?: AbortSignal): Promise<IcdRef[]>;
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

  // Standing clinical facts — blood group + allergies, held on the MEMBER's file, not on a visit.
  /**
   * Blood group and the recorded allergy list, in one gated read.
   *
   * One call and not two on purpose: each gated emr read writes a PHI-read audit event, so splitting them
   * would record a clinician's single glance at a patient as two separate accesses in the review.
   *
   * An empty `allergies` array means NOBODY HAS RECORDED ANY — never that the patient has none. Callers
   * render the two differently or they are lying to a prescriber.
   */
  memberClinicalRecord(beneficiaryId: string): Promise<MemberClinicalRecord>;
  /** The masterdata allergen catalogue — the picker's options. Small and static; fetched once on open. */
  allergenCatalogue(): Promise<AllergenOption[]>;
  /** Record an allergy on the member's file (emr:write + treating relationship, enforced server-side). */
  addAllergy(beneficiaryId: string, req: AddAllergyRequest): Promise<AllergyRecord>;
  /**
   * Set the member's blood group.
   *
   * A PUT because a person has one: recording it a second time is a correction, not a second fact. The
   * server keeps both values in the audit trail, since a CHANGED blood group is the entry a reviewer wants.
   */
  setBloodGroup(beneficiaryId: string, bloodGroup: BloodGroup): Promise<void>;

  // Lab / imaging — queue + consume (Phase 5)
  labQueue(kind: "lab" | "radiology"): Promise<LabOrder[]>;

  /**
   * 29.2 — the OP-Procedure kinds the composer offers (design 45 §2). Master data, so the list grows by a
   * data change rather than a release. The composer reveals its sessions field from `isSessionBased`.
   */
  procedureTypes(): Promise<ProcedureType[]>;

  /**
   * 29.2 — what each code will actually CREATE (design 45 §2). Read as the doctor picks, so the composer
   * can say so before they commit; `kinds` narrows to the vehicles a tab can raise.
   */
  orderableServices(query: string, kinds?: readonly string[]): Promise<OrderableService[]>;

  /**
   * 29.2 — raise a REFERRAL for an E/M code (invariant 3). The server re-derives the vehicle and refuses
   * `not-a-referral-service` for a code that routes elsewhere, so this call cannot bypass the routing map.
   */
  createReferral(req: {
    encounterId: string;
    targetSpecialty: string;
    reason?: string;
    requestedServiceCode: string;
    targetProviderId?: string | null;
  }): Promise<ReferralCreated>;

  // ---- 29.2b — the external delivering provider's portal (design 45 §2b) ----
  /** The orders routed to THIS centre. Server-scoped by assigned_provider_id; the client never filters. */
  procedureQueue(): Promise<ProcedureQueueItem[]>;
  /** Verify the person at the counter — TWO identifiers required, audited server-side. */
  procedureCounterSearch(by: { cardNumber?: string; memberNo?: string; passport?: string }): Promise<ProcedureQueueItem[]>;
  /** Record ONE delivered session. `idempotencyKey` is required: a double-tap must not burn two visits. */
  recordProcedureSession(
    orderId: string, orderLineId: string, idempotencyKey: string,
    by: { practitioner?: string; attended?: boolean; note?: string },
  ): Promise<SessionProgress>;
  /** Close the loop with a report back to the ordering doctor. */
  reportProcedureCompletion(orderId: string, findings: string): Promise<void>;

  /**
   * 29.4 — THE service-history read (design 45 §4). ONE method, one endpoint, every tab.
   *
   * <p>Composed server-side under the caller's token: a withheld field is ABSENT from the response, so this
   * signature cannot be used to fetch something the caller may not see and then choose not to show it.</p>
   */
  serviceHistory(
    beneficiaryId: string,
    q: { serviceType?: string; code: string; page?: number; pageSize?: number },
  ): Promise<ServiceHistory>;
  /**
   * Find a patient's investigation orders by order number, or by TWO of their identifiers (27.8).
   *
   * <p>The same shape as `pharmacySearch`, through the same shared beneficiary lookup, so a bench and a
   * counter answer "who is this member" identically — including on the failure paths, which are the ones
   * that matter.</p>
   */
  labSearch(
    kind: "lab" | "radiology",
    by: { orderNo?: string; cardNumber?: string; memberNo?: string; passport?: string },
  ): Promise<LabOrder[]>;
  consume(req: ConsumeRequest): Promise<ConsumeResult>;
  /** One order with every line on it — what the order page is built from (ADR-0034). */
  investigationOrder(orderNo: string): Promise<InvestigationOrder | null>;
  /**
   * What the order costs and how it splits between member and payer.
   *
   * Separate from the order itself because it is a different question with a different failure mode: the
   * order is always knowable, the price often is not. Callers MUST honour `determinate` — a `false` means
   * the amounts are unknown, NOT zero.
   */
  orderPricing(orderId: string, performNow?: Record<string, number>): Promise<OrderPricing>;
  /** Ask the approval team whether another examination may stand in. Returns the AUTH- number raised. */
  requestSubstitution(req: SubstitutionRequest, idempotencyKey?: string): Promise<{ authNo: string }>;
  /** Consumed lines this provider still owes a result on (US-042). */
  awaitingResult(kind: "lab" | "radiology"): Promise<ResultTask[]>;
  /** Attach a result value to a consumed line (US-042). */
  uploadResult(orderId: string, lineId: string, resultValue: string, idempotencyKey?: string): Promise<ResultUpload>;

  // Pharmacy — dispense (Phase 6)
  pharmacyQueue(): Promise<Prescription[]>;
  /**
   * The dispensing counter's lookup: one member's dispensable prescriptions.
   *
   * By Rx number alone, or by TWO of the member's identifiers — a card number on its own resolves nobody,
   * because it is printed on something that gets shared and photographed. The server refuses a single
   * identifier with 422 and answers 503 when it cannot reach the patient directory, so "no prescriptions"
   * only ever means no prescriptions.
   */
  pharmacySearch(by: { rxNo?: string; cardNumber?: string; memberNo?: string; passport?: string }): Promise<Prescription[]>;
  dispense(req: DispenseRequest): Promise<DispenseResult>;
  /**
   * What the prescription costs and how it splits between member and payer.
   *
   * Separate from the prescription itself because it is a different question with a different failure mode:
   * the prescription is always knowable, the price often is not. Callers MUST honour `determinate` — a
   * `false` means the amounts are unknown, not zero.
   */
  prescriptionPricing(
    prescriptionId: string,
    /**
     * What is about to be handed over, by line id. Omit to price the whole prescription.
     *
     * <p>The share is quoted on THIS, not scaled from the whole-prescription figure: the split runs a
     * deductible before a copay before coinsurance, so the member's share of 7 units is not half their share
     * of 14. Only the server may compose it — `libs/benefit-pricing` is the one place that answer is
     * allowed to come from, so that the amount a member is told at the counter and the amount their claim is
     * charged cannot diverge.</p>
     */
    dispenseNow?: Record<string, number>,
  ): Promise<RxPricing>;
  /**
   * Active ingredient by drug id, from master data. Missing ids are absent from the map.
   *
   * A client-side join, the same shape as `icdTitles` and `branchLabels`: the molecule a product contains is
   * master data's fact, and answering it from pharmacy-service would make pharmacy a second place that says
   * what a drug is.
   *
   * THROWS when the catalogue cannot be reached. An empty map means "no ingredient recorded"; a rejection
   * means "we could not ask", and a caller that cannot tell them apart will render an outage as a fact about
   * the medicine.
   */
  drugIngredients(drugIds: readonly string[]): Promise<Map<string, string>>;
  /** Formulary lookup for substitutions (US-052): search drugs, then list a drug's approved alternatives. */
  searchDrugs(query: string, signal?: AbortSignal): Promise<DrugRef[]>;
  drugAlternatives(drugId: string): Promise<DrugRef[]>;

  // Prescribing workspace (phase 26, design 43 §6)
  /**
   * Typeahead over the CURRENT market list, by trade name OR active ingredient.
   *
   * A prescriber searches by whichever name they know: "augmentin" and "amoxicillin" must both reach the
   * same product. Returns real drug uuids — the modal this replaced sent the ATC code string where the API
   * expects a Guid.
   */
  searchPrescribableDrugs(query: string, signal?: AbortSignal): Promise<PrescribableDrug[]>;
  /**
   * Step 1 — advisory validation while composing (design 43 §5).
   *
   * Its verdict is DISPLAY STATE ONLY. The server re-evaluates from scratch on submit and reads nothing
   * this returned, so a client that lied about the outcome changes nothing.
   */
  validatePrescription(req: {
    encounterId: string;
    lines: PrescriptionDraftLine[];
    diagnosisIcdCodes: string[];
  }): Promise<ValidationResult>;
  /** Submit. Every warning must carry an acknowledgement with a reason, or the server refuses with 422. */
  submitPrescription(req: {
    encounterId: string;
    lines: PrescriptionDraftLine[];
    diagnosisIcdCodes: string[];
    acknowledgements: LineAcknowledgement[];
    // 29.5 — the script's own shape (design 45 §5). Omitted entirely for an acute prescription.
    kind?: PrescriptionKind;
    refillFrequencyCode?: string | null;
    durationDays?: number | null;
  }): Promise<PrescriptionSubmitResult>;

  // ---- 29.5 — acute / chronic prescribing (design 45 §5) ----
  /** The supervisor-configurable refill cadences. ACTIVE rows only. */
  refillFrequencies(): Promise<RefillFrequency[]>;
  /**
   * The computed window schedule, BEFORE submit — so the doctor sees 34/33/33 and can adjust.
   * Computed SERVER-side by the same allocation the write path runs, so the two cannot drift.
   */
  /**
   * 29.6 — how much will actually be dispensed, before the doctor commits (design 45 §6).
   *
   * <p>Answered by the SERVER because `QuantityMath` is the one implementation of that arithmetic — the
   * validation check grades against it and the counter meters against it. Send the DRUG, not pack facts:
   * they are master data, and a client that fetched them to hand back would be a second reader of the
   * catalogue and therefore a second thing that can disagree with it.</p>
   */
  /**
   * 31.2 — one catalogue product, in the shape the composer holds.
   *
   * <p>Exists so a RESTORED draft can refresh the snapshot it saved. `useDraft` persists the whole drug
   * object, which is right — a composer that lost its medicine on reload would be worse — but it means the
   * name, the price and the lowest-price flag are frozen at the moment the line was composed. A catalogue
   * reload between then and now leaves a doctor reading last week's name and last week's price.</p>
   *
   * <p>Returns null when the product is no longer in the catalogue. The composer keeps what it had rather
   * than blanking the line: a stale name is still the medicine they chose.</p>
   */
  prescribableDrugById(drugId: string): Promise<PrescribableDrug | null>;
  quantityPreview(req: {
    drugId?: string;
    doseAmount?: number | null;
    timesPerDay?: number | null;
    durationDays?: number | null;
  }): Promise<QuantityPreview>;
  chronicPreview(req: {
    durationDays: number;
    refillFrequencyCode: string;
    doseAmount?: number;
    timesPerDay?: number;
    /** The product, so the SERVER resolves its pack facts — the same lookup the write path makes. */
    drugId?: string;
    isPackSplittable?: boolean | null;
    /** 31.5 — what one box HOLDS, renamed from `packSize`: the pack size counts CONTAINERS for every
     *  measured product and is the wrong divisor for all of them (31.3). Normally omitted — the server
     *  resolves it from `drugId`. */
    packContent?: number | null;
  }): Promise<ChronicPreview>;

  // Investigation ordering workspace — the lab / imaging counterpart of the prescribing trio above.
  /**
   * CPT typeahead for the section being ordered from.
   *
   * Section, NOT the stored `category`: the taxonomy field says "Category I" for both a chest x-ray and a
   * blood count, so it cannot separate the Labs tab from the Imaging tab. The numeric range can.
   */
  /** Typeahead over the CPT catalogue, narrowed to the sections a tab orders from (Labs spans two). */
  searchCpt(query: string, sections: readonly CptSection[], signal?: AbortSignal): Promise<CptRef[]>;
  /**
   * Ask the approval team to revalidate an expired prescription or order.
   *
   * A 409 is an ANSWER — one is already open for this item — not a failure to ask.
   */
  requestValidityExtension(req: ValidityExtensionRequest): Promise<ValidityExtensionResult>;
  /** The tenant's four validity periods, with their bounds and whether each was actually chosen. */
  validityPolicy(): Promise<ValidityPolicyView>;
  /** Set one. Applies to prescriptions and orders written from now on; existing ones keep their expiry. */
  setValidityPolicy(artefact: string, days: number): Promise<void>;
  /** Step 1 — advisory while composing. Its verdict is display state; the create path re-derives everything. */
  validateInvestigationOrder(req: {
    encounterId: string;
    orderType: InvestigationOrderType;
    lines: InvestigationDraftLine[];
    diagnosisIcdCodes: string[];
  }): Promise<OrderValidationResult>;
  /** Step 2 — one order carrying every composed line. */
  submitInvestigationOrder(req: {
    encounterId: string;
    orderType: InvestigationOrderType;
    lines: InvestigationDraftLine[];
    acknowledgements: OrderAcknowledgement[];
    /**
     * 31.1 — the OP-Procedure COURSE: one kind and one session count for the whole order.
     *
     * <p>They were per-line, which let a two-item course carry two kinds and two session counts — not a
     * course any centre can deliver. Absent on Lab and Radiology orders, which have neither.</p>
     */
    procedureTypeCode?: string | null;
    sessions?: number | null;
  }): Promise<InvestigationOrderResult>;

  // Approvals — worklist + decision (Phase 7)
  /**
   * The approval team's list.
   *
   * `kind` defaults to `Review` — the work queue, meaning "these are waiting for you". Pass `Fulfilment` for
   * the register of what has actually been handed over at counters and benches, or `All` for both. The
   * default is deliberate (ADR-0034): a few hundred dispenses a day landing in the inbox would drown the
   * twelve requests that need a decision, and a queue that is mostly noise stops being read.
   */
  approvalWorklist(kind?: "Review" | "Fulfilment" | "All"): Promise<ApprovalItem[]>;
  /** What was actually delivered against an authorization. Empty for a review request — nothing has been
   *  delivered against a question that has not been answered. */
  authorizationItems(authorizationId: string): Promise<AuthorizationItem[]>;
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

  // ---- 28.8/28.9: administering people, not just looking at them ------------------------------------------
  //
  // Every one of these existed on identity-service since 17.4 and had NO caller. The console could display a
  // user and could not create, correct, re-enable or help one — so the only way to bring somebody onto the
  // platform was a database seed. These are the wires.

  /**
   * Create an account and INVITE it.
   *
   * No password crosses this boundary in either direction. The server generates a throwaway, discards it,
   * and emails a reset link — so there is no moment at which the administrator knows the credential (28.7,
   * applied to creation). `resetLinkSent: false` means the account is real but nobody has been told about
   * it yet, which the UI must say rather than smooth over.
   */
  createIdentityUser(input: {
    username: string;
    displayName: string;
    email: string;
    tenantId: string;
    roles: string[];
    lang?: "en" | "ar";
  }): Promise<{ id: string; resetLinkSent: boolean }>;
  /** Correct a name or an address. Roles are deliberately NOT settable here — see setIdentityUserRoles. */
  updateIdentityUser(id: string, input: { displayName?: string; email?: string }): Promise<void>;
  /**
   * Set the roles — and therefore the PORTALS — this account holds.
   *
   * Takes the ISSUER's role names (`lab_tech`, `pharmacist`), not the SPA's portal keys. `issuerRoleFor`
   * in config.ts is the translation; sending portal keys is a 422 for every clinical role in the system.
   */
  setIdentityUserRoles(id: string, roles: string[]): Promise<void>;
  /** Soft deprovision: the account, its membership and every live session. Never a delete. */
  deactivateIdentityUser(id: string): Promise<void>;
  /** The way back. Sessions are NOT restored — the person signs in again, and gets a fresh token. */
  reactivateIdentityUser(id: string): Promise<void>;
  /** Start a reset for somebody who cannot start their own. Issues a link; never reveals a password. */
  sendPasswordResetLink(id: string, lang?: "en" | "ar"): Promise<void>;
  /** Change MY password. Requires the current one — a live token proves the device, not the owner. */
  changeMyPassword(currentPassword: string, newPassword: string): Promise<void>;

  /** 28.9 — every permission in the system, with its flags and who already holds it. */
  scopeCatalog(): Promise<ScopeCatalogEntry[]>;
  /** 28.9 — every role this tenant may assign, built-in and its own, with what each actually grants. */
  roleCatalog(): Promise<RoleCatalogEntry[]>;
  /** 28.9 — design a role from the catalogue. Refused 409 if the set holds both halves of a split duty. */
  createRole(input: {
    name: string;
    scopes: string[];
    description?: string;
    sensitivityTier?: string;
  }): Promise<void>;
  /** Replace a role's permission set, in this tenant only. Built-in and custom roles both. */
  setRoleScopes(role: string, scopes: string[]): Promise<void>;
  /** 18.C2 (W5) — the live role→scope matrix the token issuer actually reads. */
  identityRoleScopes(): Promise<RoleScopeGrant[]>;
  accessMatrix(): Promise<RoleBinding[]>;
  adminTenants(): Promise<TenantSummary[]>;
  sodMatrix(): Promise<SodConflict[]>;
  accessReviewCampaigns(): Promise<AccessReviewCampaign[]>;
  breakGlassGrants(): Promise<BreakGlassGrant[]>;
  /** The tenant's auto-decision kill switch (ADR-0035 §5.3). Never touched reads `enabled: false`. */
  autoDecisionSwitch(): Promise<AutoDecisionSwitch>;
  /** Turn auto-decision on or off. A reason is required in both directions, and it is audited. */
  setAutoDecision(req: SetAutoDecision): Promise<AutoDecisionSwitch>;
  /** The engine's routing and SLA rules, plus the queues a routing rule may target (ADR-0035 §5). */
  approvalRules(family?: "Routing" | "Sla" | "Preauth" | "AutoApprove"): Promise<ApprovalRuleList>;
  /** Publish a rule. Supplying `supersedesRuleId` closes the prior version rather than editing it. */
  saveApprovalRule(req: SaveApprovalRule): Promise<{ id: string; versionNo: number }>;
  /** The tenant's document validity policy — every kind answered whether configured or not (ADR-0035 §6). */
  adminDocumentValidity(): Promise<DocumentValidityView>;
  /** Set a cadence, thresholds, or both. Omitting one leaves it untouched rather than clearing it. */
  adminSetDocumentValidity(req: SetDocumentValidity): Promise<void>;
  /**
   * Append a new effective-dated version of a master-data code (ADR-0035 §4).
   *
   * <p>Never an update. The prior version's window closes and a new one opens, so a record written last March
   * still resolves the code as it read last March — which is the whole reason master-data edits go through
   * this governance path instead of a write on masterdata-service.</p>
   */
  adminMasterDataUpsert(edit: MasterDataEdit): Promise<{ id: string; code: string; versionNo: number }>;
  /** The version in force at an instant — the "what did this mean then" read behind the editor's diff. */
  adminMasterDataAsOf(system: string, code: string, at: string): Promise<MasterDataAsOf>;
  adminMasterData(): Promise<MasterDataVersion[]>;
  adminSystemConfig(): Promise<SystemConfigEntry[]>;
  /**
   * Set one system-config value — 28.10.
   *
   * <p>The endpoint has existed since 8b.2 (typed, validated, effective-dated, audited) and nothing in the
   * SPA has ever called it, so every one of these settings was read-only in the product and changeable only
   * by a hand-written SQL statement against `admin.system_config`. That is the shape of a "hardcoded value":
   * not a literal in the source, but a row nobody was given a way to reach.</p>
   *
   * <p>Returns the NEW version. The prior one is not overwritten — its window closes — so the version number
   * coming back is the evidence the append happened rather than a no-op.</p>
   */
  adminSystemConfigSet(edit: SystemConfigEdit): Promise<SystemConfigEntry>;

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
  /**
   * The approver's queue (US-003): Pending beneficiaries + their latest application. Oldest first.
   *
   * Returns a PAGE plus the size of the whole queue. The screen searches, filters, sorts and paginates the
   * loaded page in the browser — instant, and incapable of disagreeing with what is on screen — and uses
   * `total` to say plainly when the queue is larger than what it has. See `RegistrationApprovals`.
   */
  registrationWorklist(pageSize?: number): Promise<RegistrationWorklistPage>;
  /** The conversation about one application: every decision, and every reply to one. Oldest first. */
  registrationThread(id: string): Promise<RegistrationThreadEntry[]>;
  /** Answer a decision — the officer supplying what was asked for, or the supervisor following up. */
  replyToRegistration(id: string, body: string): Promise<RegistrationThreadEntry>;
  /** The paperwork filed against the person, as metadata. No bytes: opening a scan is its own disclosure. */
  beneficiaryDocuments(beneficiaryId: string): Promise<BeneficiaryDocument[]>;
  /**
   * ONE person's identity record, field-projected by role and audited as a PHI read by the server.
   *
   * Distinct from the roster's per-page summary, which carries name + status + card number and nothing else:
   * a list is the highest-volume disclosure the platform makes, so the rest of the record is read one person
   * at a time, through here.
   */
  beneficiary(id: string): Promise<BeneficiaryRow>;
  /**
   * Correct the identity record (US-002). PARTIAL — only the keys present are written, so a form showing five
   * fields cannot blank the four it did not. Every change is audited with its before/after and lands on the
   * member's Logs.
   */
  updateBeneficiary(id: string, edit: BeneficiaryEdit): Promise<{ changed: string[] }>;
  /** Open an application for a beneficiary that has none (legacy rows) or whose last one was Rejected. */
  createRegistration(beneficiaryId: string, idempotencyKey?: string): Promise<void>;
  /** The officer's preparation step — the two approval guards the server checks before Approve. */
  setRegistrationChecks(id: string, checks: { documentsVerified?: boolean; coverageBound?: boolean }): Promise<void>;
  /**
   * The approver's decision. Supervisor-only ON THE SERVER (urn:hbmp:approver-required) — the officer who
   * vouched for the documents must not be the one who activates. Approve returns the issued member number.
   */
  decideRegistration(id: string, decision: "Approve" | "RequestInfo" | "Reject", notes?: string): Promise<RegistrationDecisionResult>;
  /**
   * The same decision over many applications.
   *
   * Deliberately a LOOP of single decisions rather than one bulk endpoint. Each row keeps its own audit
   * event, its own idempotency and its own server-side guard check — so an Approve the server refuses because
   * coverage is not bound fails that row and only that row, and the caller is told which. A bulk endpoint
   * would have to reproduce all three, and its all-or-nothing failure mode is the wrong one here: refusing
   * nine good approvals because the tenth was not ready is not safer, it is just slower.
   *
   * Never rejects. Per-row outcomes come back in `ok`/`error`, because a thrown error would discard the
   * results of the rows that succeeded before it.
   */
  decideRegistrations(
    ids: readonly string[],
    decision: "Approve" | "RequestInfo" | "Reject",
    notes?: string,
  ): Promise<BulkDecisionOutcome[]>;
}

/**
 * Fault injection for the dev/test client. `latencyMs` drives the loading state; `fault` lets a test render a
 * screen straight into its empty or error branch without a live backend.
 */
export interface ApiScenario {
  latencyMs?: number;
  fault?: "none" | "error" | "empty";
  /**
   * The ISSUER roles the fixture should answer as, for the endpoints the server role-projects (currently the
   * patient profile). A function rather than a value because the signed-in role changes without the client
   * being rebuilt — the dev login picks one after `ApiProvider` has already constructed this.
   *
   * Returning an empty array means "no role known", and the fixture then answers with everything rather than
   * nothing. That is a FIXTURE convenience, not a policy: it keeps a client constructed with no session
   * usable, and the enforcement that matters is server-side and tested there.
   */
  roles?: () => readonly string[];
}
