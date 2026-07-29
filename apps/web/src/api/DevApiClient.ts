import { z } from "zod";
import {
  zApprovalItem,
  zApprovalReview,
  zConsumeResult,
  zDecisionResult,
  zDispenseResult,
  zEligibilityHit,
  zEligibilityResult,
  zEncounter,
  zExecutiveDashboard,
  zLabOrder,
  zPatientListItem,
  zPlaceOrderResult,
  zPrescribeResult,
  zPrescription,
  zBeneficiary360,
  zPatientProfile,
  zCopySummariesResult,
  zProfileExportSummary,
  type ProfileSectionKey,
  zCaseListItem,
  zCoordinationTask,
  zEscalation,
  zNotification,
  zMarkAllReadResult,
  zMarkReadResult,
  zRoleBinding,
  zTenantSummary,
  zSodConflict,
  zAccessReviewCampaign,
  zAppointmentRow,
  zBookableClinic,
  zTimelineStep,
  zBookableSlot,
  zBookingResult,
  zBreakGlassGrant,
  zMasterDataVersion,
  zSystemConfigEntry,
  zRegistrationWorkItem,
  zRegistrationDecisionResult,
  zMembershipRow,
  zMembershipDetail,
  zEffectiveAccess,
  zBranchScopeGrant,
  zAccessSession,
  zProgramEnablement,
  zProviderSummary,
  zProviderLocation,
  zProviderContract,
  type CreateProviderInput,
  zBeneficiaryRow,
  zRegisterResult,
  zStatusChangeResult,
  type RegisterBeneficiaryInput,
  zCheckInResult,
  zOrderRow,
  zRxRow,
  zResultDetail,
  zReportAccessRequestResult,
  type ReportAccessInput,
  zClaimRow,
  zReconciliationRow,
  zClaimsKpis,
  zVitalsResult,
  zResultTask,
  zResultUpload,
  zDrugRef,
  zTatSummary,
  zManualAuthResult,
  zEmergencyResult,
  type ManualAuthInput,
  zReportView,
  type VitalInput,
  zExportResult,
  zFinancialSummary,
  zSettlement,
  zUtilizationView,
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type ExportRequest,
  type Localized,
  type PlaceOrderRequest,
  type PrescribeRequest,
  type IdentityUser,
  type RoleScopeGrant,
  type ReportAccessRequestRow,
} from "@mersal/contracts";
import type { BookingRequest } from "@mersal/contracts";
import type { ApiClient, ApiScenario } from "./client";
import { ApiError } from "./http";

const loc = (en: string, ar: string): Localized => ({ en, ar });

/** The in-app inbox fixture. Module-level so the read-state overlay and the unread count read one source. */
const NOTIFICATION_FIXTURE = [
  {
    id: "NTF-1",
    subject: "Authorization awaiting review",
    body: "A new authorization is on your worklist and awaiting a decision.",
    status: { kind: "warn" as const, label: loc("Action needed", "إجراء مطلوب") },
    entityRef: "AUTH-2026-0001",
    sourceEventType: "AuthorizationSubmitted",
    actionable: true,
    read: false,
    createdAt: "2026-07-22T07:30:00Z",
  },
  {
    id: "NTF-2",
    subject: "Authorization approved",
    body: "An authorization you requested has been approved.",
    status: { kind: "ok" as const, label: loc("Approved", "معتمد") },
    entityRef: "AUTH-2026-0002",
    sourceEventType: "AuthorizationDecided",
    actionable: false,
    read: true,
    createdAt: "2026-07-21T12:00:00Z",
  },
];
const NOW = "2026-07-22T08:30:00Z";

/** Validate every fixture through its schema on the way out — a fixture that drifts from the contract fails loudly. */
function ok<T>(schema: z.ZodType<T>, data: unknown): T {
  const r = schema.safeParse(data);
  if (!r.success) throw new ApiError("schema", `Dev fixture violates contract: ${r.error.issues[0]?.message}`);
  return r.data;
}

/**
 * In-memory fixture client — the same shape a real OIDC-gated HTTP client will have, backed by synthetic,
 * bilingual, contract-valid data (never real PHI). It drives the dev app and the tests: `latencyMs` exercises
 * the loading state, `fault` renders a screen straight into empty/error, and a repeated Idempotency-Key
 * returns `replayed: true` instead of double-applying (proving the consume/dispense/decide contract).
 */
export class DevApiClient implements ApiClient {
  private seenKeys = new Set<string>();
  private labProgress = new Map<string, number>(); // orderId → panelsDone
  private rxProgress = new Map<string, Map<string, number>>(); // rxId → lineId → dispensed
  private notificationsRead = new Set<string>(); // notification ids marked read in this session

  constructor(private scenario: ApiScenario = { latencyMs: 0, fault: "none" }) {}

  private async gate<T>(build: () => T, emptyValue?: T): Promise<T> {
    const { latencyMs = 0, fault = "none" } = this.scenario;
    if (latencyMs > 0) await new Promise((r) => setTimeout(r, latencyMs));
    if (fault === "error") throw new ApiError("http", "Simulated upstream failure", 503);
    if (fault === "empty" && emptyValue !== undefined) return emptyValue;
    return build();
  }

  // ---- Eligibility -------------------------------------------------------
  searchEligibility(query: string) {
    return this.gate<ReturnType<typeof this.buildHits>>(() => this.buildHits(query), []);
  }
  private buildHits(query: string) {
    const all = [
      { id: "MRS-M-10231", name: loc("Amal Hassan", "أمل حسن"), cardNumber: "•••• 4821" },
      { id: "MRS-M-10555", name: loc("Yusuf Haddad", "يوسف حداد"), cardNumber: "•••• 7702" },
    ];
    const q = query.toLowerCase();
    return ok(
      z.array(zEligibilityHit),
      all.filter((h) => h.name.en.toLowerCase().includes(q) || h.id.toLowerCase().includes(q) || q.length >= 2),
    );
  }
  checkEligibility(beneficiaryId: string) {
    return this.gate(() =>
      ok(zEligibilityResult, {
        verdict: "eligible",
        status: { kind: "ok", label: loc("Eligible", "مؤهل") },
        beneficiary: {
          id: beneficiaryId,
          name: loc("Amal Hassan", "أمل حسن"),
          cardNumber: "MRS-CARD-4821",
          dateOfBirth: "1990-04-12",
          gender: "female",
        },
        coverage: {
          planName: loc("Mersal Essential", "مرسال الأساسية"),
          band: loc("Band B — Outpatient + Pharmacy", "الفئة ب — عيادات + صيدلية"),
          validUntil: "2026-12-31",
          copayPercent: 10,
          annualCapRemaining: 8400,
        },
        visitGate: { allowed: true },
      }),
    );
  }

  // ---- Reception day board -----------------------------------------------
  appointments(filter: "all" | "booked" | "checked-in" = "all", _mine = false) {
    void _mine;
    const rows = [
      { id: "appt-1", token: "•••4821", type: "Consultation", ar: "كشف", st: "Booked", chip: { kind: "info" as const, label: loc("Booked", "محجوز") }, at: "2026-07-22T09:00:00Z", eligible: true },
      { id: "appt-2", token: "•••7710", type: "FollowUp", ar: "متابعة", st: "CheckedIn", chip: { kind: "ok" as const, label: loc("Checked in", "تم الوصول") }, at: "2026-07-22T09:30:00Z", eligible: false },
            // Its window has passed by more than the grace period, so the SERVER would allow a no-show here.
      { id: "appt-3", token: "•••2093", type: "Consultation", ar: "كشف", st: "Booked", chip: { kind: "info" as const, label: loc("Booked", "محجوز") }, at: "2026-07-22T10:00:00Z", eligible: true, noShowEligible: true },
      { id: "appt-4", token: "•••5540", type: "Procedure", ar: "إجراء", st: "NoShow", chip: { kind: "warn" as const, label: loc("No-show", "لم يحضر") }, at: "2026-07-22T08:30:00Z", eligible: false },
    ].filter((r) => (filter === "booked" ? r.st === "Booked" : filter === "checked-in" ? r.st === "CheckedIn" : true));
    return this.gate(
      () =>
        ok(z.array(zAppointmentRow), rows.map((r) => ({
          id: r.id,
          beneficiary: { id: r.id, token: r.token },
          appointmentType: r.type,
          status: r.chip,
          scheduledStart: r.at,
          checkInEligible: r.eligible,
          checkedIn: r.st === "CheckedIn",
          noShowEligible: r.noShowEligible ?? false,
          startVisitEligible: r.st === "CheckedIn",
          rowVersion: 1,
        }))),
      [],
    );
  }
  appointmentTimeline(_appointmentId: string) {
    void _appointmentId;
    return this.gate(
      () =>
        ok(z.array(zTimelineStep), [
          { status: "Booked", at: "2026-07-22T08:00:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Nada Reception" },
          // Deliberately unattributed: a step recorded before actor attribution existed.
          { status: "CheckedIn", at: "2026-07-22T08:55:00Z", by: null, byName: null },
          // An actor whose name could not be resolved (deactivated, or another tenant) — the id still shows.
          { status: "Completed", at: "2026-07-22T09:40:00Z", by: "129d2a05-8c27-43c7-aae2-f2cc4c7fda30", byName: null },
        ]),
      [],
    );
  }
  startVisit(_appointmentId: string, _beneficiaryId: string) {
    void _appointmentId; void _beneficiaryId;
    return this.gate(() => ({ encounterId: "enc-1" }));
  }
  noShow(appointmentId: string, _rowVersion?: number) {
    void _rowVersion;
    return this.gate(() => ok(zCheckInResult, { id: appointmentId, status: { kind: "warn", label: loc("No-show", "لم يحضر") } }));
  }
  checkIn(appointmentId: string, _rowVersion?: number) {
    void _rowVersion; // fixture path applies no concurrency guard; the live client echoes it as If-Match.
    return this.gate(() => ok(zCheckInResult, { id: appointmentId, status: { kind: "ok", label: loc("Checked in", "تم الوصول") } }));
  }

  // ---- Booking -----------------------------------------------------------
  bookableClinics(_branchId?: string) {
    void _branchId;
    return this.gate(
      () =>
        ok(z.array(zBookableClinic), [
          { providerId: "prov-1", locationId: "loc-1", branchId: "br-dokki", label: "Mersal Dokki · Dokki Clinic", openSlots: 2 },
        ]),
      [],
    );
  }
  // One slot is deliberately CLOSED: the desk must render availability from the server's answer, and a
  // fixture where everything is bookable would never exercise that.
  openSlots(_providerId: string, _locationId: string, _from?: string, _to?: string) {
    void _providerId; void _locationId; void _from; void _to;
    return this.gate(
      () =>
        ok(z.array(zBookableSlot), [
          { id: "slot-1", start: "2026-07-22T11:00:00Z", end: "2026-07-22T11:15:00Z", open: true },
          { id: "slot-2", start: "2026-07-22T11:15:00Z", end: "2026-07-22T11:30:00Z", open: false },
          { id: "slot-3", start: "2026-07-22T11:30:00Z", end: "2026-07-22T11:45:00Z", open: true },
        ]),
      [],
    );
  }
  bookAppointment(input: BookingRequest) {
    return this.gate(() =>
      ok(zBookingResult, {
        id: `appt-${input.slotId}`,
        status: { kind: "info", label: loc("Booked", "محجوز") },
        scheduledStart: "2026-07-22T11:00:00Z",
      }),
    );
  }

  // ---- EMR ---------------------------------------------------------------
  listPatients() {
    return this.gate(
      () =>
        ok(z.array(zPatientListItem), [
          {
            id: "MRS-M-10231",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000231",
            name: loc("Amal Hassan", "أمل حسن"),
            mrn: "MRN-10231",
            treating: true,
            lastVisit: "2026-07-01",
            status: { kind: "ok", label: loc("In consultation", "في الكشف") },
          },
          {
            id: "MRS-M-10555",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000555",
            name: loc("Yusuf Haddad", "يوسف حداد"),
            mrn: "MRN-10555",
            treating: true,
            lastVisit: "2026-06-20",
            status: { kind: "info", label: loc("Waiting", "بالانتظار") },
          },
        ]),
      [],
    );
  }
  getEncounter(patientId: string) {
    return this.gate(() =>
      ok(zEncounter, {
        id: "ENC-88120",
        patientId,
        patientName: loc("Amal Hassan", "أمل حسن"),
        openedAt: NOW,
        signed: false,
        soap: {
          subjective: "Persistent cough for 5 days, low-grade fever.",
          objective: "Temp 37.8°C, chest clear, no distress.",
          assessment: "Suspected upper respiratory infection.",
          plan: "Supportive care; CBC to rule out bacterial cause.",
        },
        vitals: { heightCm: 164, weightKg: 61, systolic: 118, diastolic: 76, heartRate: 82, tempC: 37.8 },
        allergies: [{ id: "AL-1", substance: loc("Penicillin", "بنسلين"), severity: "moderate" }],
        diagnoses: [{ system: "ICD-10", code: "J06.9", label: loc("Acute upper respiratory infection", "التهاب تنفسي علوي حاد") }],
      }),
    );
  }
  placeOrder(req: PlaceOrderRequest) {
    return this.gate(() =>
      ok(zPlaceOrderResult, {
        orderId: "ORD-55012",
        status: { kind: "info", label: loc("Order placed", "تم الطلب") },
        requiresApproval: req.priority === "routine" ? false : true,
      }),
    );
  }
  prescribe(req: PrescribeRequest) {
    return this.gate(() =>
      ok(zPrescribeResult, {
        prescriptionId: "RX-33110",
        status: { kind: "ok", label: loc("Prescription submitted", "تم إرسال الوصفة") },
        advisories:
          req.drug.code === "J01CA04"
            ? [loc("Patient has a moderate penicillin allergy — review.", "لدى المريض حساسية متوسطة من البنسلين — راجع.")]
            : [],
      }),
    );
  }

  ordersMine(status?: string) {
    const rows = [
      { id: "ord-1", line: "ln-1", no: "ORD-2026-000118", tok: "•••4821", type: "Lab", code: "80053", n: 1, st: { kind: "info" as const, label: loc("Active", "نشط") }, key: "Active", at: "2026-07-22T08:10:00Z" },
      // ord-2 is a psychiatry-panel result → sensitivity-restricted (14.7); resultDetail returns existence-only.
      { id: "ord-2", line: "ln-2", no: "ORD-2026-000119", tok: "•••7710", type: "Imaging", code: "71046", n: 1, st: { kind: "ok" as const, label: loc("Completed", "مكتمل") }, key: "Completed", at: "2026-07-21T14:00:00Z" },
      { id: "ord-3", line: "ln-3", no: "ORD-2026-000120", tok: "•••2093", type: "Lab", code: "85025", n: 2, st: { kind: "ok" as const, label: loc("Completed", "مكتمل") }, key: "Completed", at: "2026-07-20T09:30:00Z" },
    ].filter((r) => !status || r.key === status);
    return this.gate(
      () =>
        ok(z.array(zOrderRow), rows.map((r) => ({
          id: r.id, orderNo: r.no, beneficiary: { id: r.id, token: r.tok },
          orderType: r.type, primaryCode: r.code, lineCount: r.n, status: r.st, requestedAt: r.at,
          firstLineId: r.line,
        }))),
      [],
    );
  }

  /** 14.6/14.7 — a completed result read. Line `ln-2` is sensitivity-restricted → existence-only metadata (no values). */
  resultDetail(orderId: string, lineId: string) {
    return this.gate(() => {
      if (lineId === "ln-2")
        return ok(zResultDetail, {
          restricted: true as const, orderId, lineId, category: "Radiology — Psychiatry protocol",
          status: "Completed", sensitivityLevel: "Sensitive", orderingBranch: "Maadi", date: "2026-07-21",
        });
      return ok(zResultDetail, {
        restricted: false as const, orderId, lineId, category: "Laboratory — Chemistry panel",
        code: "80053", value: "Within reference range", status: "Completed", resultedAt: "2026-07-20T11:00:00Z",
      });
    });
  }

  /**
   * 18.C2 (W4) — fixture approver inbox. Two rows, deliberately: one Requested and one UnderReview, so the
   * screen's status handling and the "already picked up by someone" case are both exercised without a backend.
   */
  async reportAccessInbox(): Promise<ReportAccessRequestRow[]> {
    return [
      {
        requestId: "rar-1", orderId: "ord-77", orderLineId: "ol-77a", beneficiaryToken: "•••4821",
        requestedBy: "dr.hala", requestedForRole: "doctor", purposeCode: "TRT",
        justification: "Patient referred to me for follow-up; need the histology to plan treatment.",
        requestedTtlHours: 24,
        status: { kind: "warn", label: { en: "Awaiting decision", ar: "بانتظار القرار" } },
        createdAt: new Date(Date.now() - 3_600_000).toISOString(),
      },
      {
        requestId: "rar-2", orderId: "ord-91", orderLineId: "ol-91c", beneficiaryToken: "•••1903",
        requestedBy: "dr.omar", requestedForRole: "medical_approval", purposeCode: "PUR",
        justification: "Authorization review — medical necessity for the requested procedure.",
        requestedTtlHours: 8,
        status: { kind: "info", label: { en: "Under review", ar: "قيد المراجعة" } },
        createdAt: new Date(Date.now() - 7_200_000).toISOString(),
      },
    ];
  }

  async decideReportAccess(): Promise<void> {}
  async revokeReportAccessGrant(): Promise<void> {}

  /** 18.C2 (W5) — fixture identity users. One account WITHOUT a second factor, because that is the row the
   * screen exists to make visible. */
  async identityUsers(): Promise<IdentityUser[]> {
    return [
      { id: "u-1", username: "org.admin", displayName: "Org Admin", tenantId: "•••1111", isActive: true, twoFactorEnabled: true, roles: ["org_admin"] },
      { id: "u-2", username: "dr.hala", displayName: "Dr. Hala", tenantId: "•••1111", isActive: true, twoFactorEnabled: false, roles: ["doctor"] },
      { id: "u-3", username: "left.staff", displayName: "Former Staff", tenantId: "•••1111", isActive: false, twoFactorEnabled: true, roles: ["reception"] },
    ];
  }

  async identityRoleScopes(): Promise<RoleScopeGrant[]> {
    return [
      { role: "reception", scopes: ["reception:search", "reception:read", "patient:read", "eligibility:check"] },
      { role: "doctor", scopes: ["emr:read", "emr:write", "orders:write", "rx:write", "rx:read", "patient:read"] },
      { role: "claims_officer", scopes: ["claims:read", "claims:review", "claims:decide", "claims:batch"] },
    ];
  }

  /** 14.8 — a report-access request; the server would enqueue it for the author/MD to grant. */
  requestReportAccess(input: ReportAccessInput) {
    void input;
    return this.gate(() => ok(zReportAccessRequestResult, { requestId: "rar-dev-1", status: "Pending" }));
  }
  prescriptionsMine(status?: string) {
    void status;
    return this.gate(
      () =>
        ok(z.array(zRxRow), [
          { id: "rx-1", beneficiary: { id: "rx-1", token: "•••4821" }, lineCount: 2, status: { kind: "ok", label: loc("Approved", "معتمدة") }, submittedAt: "2026-07-22T08:15:00Z" },
          { id: "rx-2", beneficiary: { id: "rx-2", token: "•••2093" }, lineCount: 1, status: { kind: "part", label: loc("Partially dispensed", "صُرفت جزئياً") }, submittedAt: "2026-07-21T10:00:00Z" },
        ]),
      [],
    );
  }

  recordVitals(encounterId: string, readings: VitalInput[]) {
    return this.gate(() => ok(zVitalsResult, { encounterId, recorded: readings.length }));
  }

  // ---- Lab / imaging -----------------------------------------------------
  labQueue(kind: "lab" | "imaging") {
    return this.gate(() => {
      const base =
        kind === "lab"
          ? [
              {
                id: "ORD-55012",
                kind: "lab" as const,
                test: { system: "LOINC" as const, code: "58410-2", label: loc("Complete blood count", "تعداد دم كامل") },
                patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
                priority: "routine" as const,
                status: { kind: "info" as const, label: loc("Queued", "في الطابور") },
                placedAt: NOW,
                panelsTotal: 3,
                panelsDone: this.labProgress.get("ORD-55012") ?? 0,
              },
              {
                id: "ORD-55019",
                kind: "lab" as const,
                test: { system: "LOINC" as const, code: "2345-7", label: loc("Glucose", "سكر الدم") },
                patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
                priority: "urgent" as const,
                status: { kind: "warn" as const, label: loc("Urgent", "عاجل") },
                placedAt: NOW,
                panelsTotal: 1,
                panelsDone: this.labProgress.get("ORD-55019") ?? 0,
              },
            ]
          : [
              {
                id: "ORD-77003",
                kind: "imaging" as const,
                test: { system: "CPT" as const, code: "71046", label: loc("Chest X-ray", "أشعة صدر") },
                patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
                priority: "routine" as const,
                status: { kind: "info" as const, label: loc("Queued", "في الطابور") },
                placedAt: NOW,
                panelsTotal: 1,
                panelsDone: this.labProgress.get("ORD-77003") ?? 0,
              },
            ];
      return ok(z.array(zLabOrder), base);
    }, []);
  }
  consume(req: ConsumeRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      const totals: Record<string, number> = { "ORD-55012": 3, "ORD-55019": 1, "ORD-77003": 1 };
      const total = totals[req.orderId] ?? 1;
      let done = this.labProgress.get(req.orderId) ?? 0;
      if (!replayed) {
        this.seenKeys.add(req.idempotencyKey);
        done = Math.min(total, done + req.panels);
        this.labProgress.set(req.orderId, done);
      }
      return ok(zConsumeResult, {
        orderId: req.orderId,
        fulfillmentId: "FUL-" + req.orderId,
        status:
          done >= total
            ? { kind: "ok", label: loc("Fulfilled", "مكتمل") }
            : { kind: "part", label: loc("Partially fulfilled", "مكتمل جزئياً") },
        panelsDone: done,
        panelsTotal: total,
        replayed,
      });
    });
  }

  awaitingResult(kind: "lab" | "imaging") {
    const rows =
      kind === "lab"
        ? [{ orderId: "ORD-55012", lineId: "L-1", orderNo: "ORD-2026-000118", code: "80053", desc: "Comprehensive metabolic panel", tok: "•••4821" }]
        : [{ orderId: "ORD-77003", lineId: "L-9", orderNo: "ORD-2026-000119", code: "71046", desc: "Chest X-ray", tok: "•••7710" }];
    return this.gate(
      () =>
        ok(z.array(zResultTask), rows.map((r) => ({
          orderId: r.orderId, lineId: r.lineId, orderNo: r.orderNo, orderType: kind === "lab" ? "Lab" : "Imaging",
          beneficiary: { id: r.orderId, token: r.tok }, code: r.code, description: r.desc, consumedAt: NOW,
        }))),
      [],
    );
  }
  uploadResult(orderId: string, lineId: string, resultValue: string) {
    void resultValue;
    return this.gate(() => ok(zResultUpload, { orderId, lineId, uploaded: true }));
  }

  searchDrugs(query: string) {
    const all = [
      { drugId: "d-amox-500", name: loc("Amoxicillin 500mg caps", "أموكسيسيلين 500مجم"), atcCode: "J01CA04", form: "Capsule", strength: "500mg" },
      { drugId: "d-amox-250", name: loc("Amoxicillin 250mg caps", "أموكسيسيلين 250مجم"), atcCode: "J01CA04", form: "Capsule", strength: "250mg" },
      { drugId: "d-metf-500", name: loc("Metformin 500mg", "ميتفورمين 500مجم"), atcCode: "A10BA02", form: "Tablet", strength: "500mg" },
    ].filter((d) => d.name.en.toLowerCase().includes(query.toLowerCase()));
    return this.gate(() => ok(z.array(zDrugRef), all), []);
  }
  drugAlternatives(drugId: string) {
    const alts = drugId.startsWith("d-amox")
      ? [
          { drugId: "d-amox-250", name: loc("Amoxicillin 250mg caps", "أموكسيسيلين 250مجم"), atcCode: "J01CA04", form: "Capsule", strength: "250mg" },
          { drugId: "d-amox-susp", name: loc("Amoxicillin 125mg/5ml susp", "أموكسيسيلين شراب"), atcCode: "J01CA04", form: "Suspension", strength: "125mg/5ml" },
        ]
      : [];
    return this.gate(() => ok(z.array(zDrugRef), alts), []);
  }

  // ---- Pharmacy ----------------------------------------------------------
  pharmacyQueue() {
    return this.gate(() => {
      const disp = (rx: string, line: string) => this.rxProgress.get(rx)?.get(line) ?? 0;
      return ok(z.array(zPrescription), [
        {
          id: "RX-33110",
          patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
          prescriber: { label: loc("Dr. N. Fahmy", "د. ن. فهمي") },
          submittedAt: NOW,
          status: { kind: "info", label: loc("Submitted", "مُرسلة") },
          lines: [
            {
              id: "RXL-1",
              drug: { system: "ATC", code: "J01CA04", label: loc("Amoxicillin 500mg", "أموكسيسيلين ٥٠٠ملغ") },
              quantity: 21,
              dispensed: disp("RX-33110", "RXL-1"),
              dose: "1 cap × 3/day",
              status: { kind: "info", label: loc("Pending", "معلّقة") },
              outOfStock: false,
            },
            {
              id: "RXL-2",
              drug: { system: "ATC", code: "R05CB", label: loc("Guaifenesin syrup", "شراب جوايفينيسين") },
              quantity: 1,
              dispensed: disp("RX-33110", "RXL-2"),
              dose: "10 ml × 3/day",
              status: { kind: "warn", label: loc("Out of stock", "غير متوفر") },
              outOfStock: true,
            },
          ],
        },
      ]);
    }, []);
  }
  dispense(req: DispenseRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      if (!replayed) {
        this.seenKeys.add(req.idempotencyKey);
        const lines = this.rxProgress.get(req.prescriptionId) ?? new Map<string, number>();
        for (const l of req.lines) lines.set(l.lineId, (lines.get(l.lineId) ?? 0) + l.quantity);
        this.rxProgress.set(req.prescriptionId, lines);
      }
      const totals: Record<string, number> = { "RXL-1": 21, "RXL-2": 1 };
      const lines = this.rxProgress.get(req.prescriptionId) ?? new Map();
      const outstanding = Object.entries(totals).filter(([id, t]) => (lines.get(id) ?? 0) < t).length;
      return ok(zDispenseResult, {
        prescriptionId: req.prescriptionId,
        dispenseEventId: "DSP-" + req.prescriptionId,
        status: outstanding === 0
          ? { kind: "ok", label: loc("Fully dispensed", "تم الصرف بالكامل") }
          : { kind: "part", label: loc("Partially dispensed", "صرف جزئي") },
        replayed,
        linesOutstanding: outstanding,
      });
    });
  }

  // ---- Approvals ---------------------------------------------------------
  approvalWorklist() {
    return this.gate(
      () =>
        ok(z.array(zApprovalItem), [
          {
            id: "AUTH-9001",
            patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
            service: { system: "CPT", code: "70553", label: loc("MRI brain w/ contrast", "رنين مغناطيسي للمخ") },
            requestedBy: loc("Nile Imaging Center", "مركز النيل للأشعة"),
            priority: "urgent",
            sla: { dueAt: "2026-07-22T12:00:00Z", breached: false, minutesRemaining: 210 },
            status: { kind: "info", label: loc("Awaiting review", "بانتظار المراجعة") },
            submittedAt: NOW,
            estimatedCost: "EGP 6,500",
          },
          {
            id: "AUTH-9002",
            patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
            service: { system: "CPT", code: "29881", label: loc("Knee arthroscopy", "منظار الركبة") },
            requestedBy: loc("Cairo Ortho Clinic", "عيادة القاهرة للعظام"),
            priority: "routine",
            sla: { dueAt: "2026-07-22T06:00:00Z", breached: true, minutesRemaining: -150 },
            status: { kind: "warn", label: loc("SLA breached", "تجاوز المهلة") },
            submittedAt: "2026-07-21T09:00:00Z",
            estimatedCost: "EGP 18,000",
          },
        ]),
      [],
    );
  }
  approvalReview(approvalId: string) {
    return this.gate(() =>
      ok(zApprovalReview, {
        id: approvalId,
        patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
        service: { system: "CPT", code: "70553", label: loc("MRI brain w/ contrast", "رنين مغناطيسي للمخ") },
        clinicalJustification:
          "Progressive headache with focal neurological signs; rule out intracranial lesion.",
        supportingCodes: [
          { system: "ICD-10", code: "R51.9", label: loc("Headache, unspecified", "صداع غير محدد") },
        ],
        documents: [
          { id: "DOC-1", name: "neuro-exam.pdf" },
          { id: "DOC-2", name: "referral-letter.pdf" },
        ],
        requestedAmount: "EGP 6,500",
      }),
    );
  }
  decide(req: DecisionRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      if (!replayed) this.seenKeys.add(req.idempotencyKey);
      const label: Record<DecisionRequest["decision"], Localized> = {
        approve: loc("Approved", "تمت الموافقة"),
        partial: loc("Partially approved", "موافقة جزئية"),
        reject: loc("Rejected", "مرفوض"),
        request_info: loc("Information requested", "طُلبت معلومات"),
      };
      const kind = req.decision === "reject" ? "bad" : req.decision === "approve" ? "ok" : "part";
      return ok(zDecisionResult, {
        approvalId: req.approvalId,
        decisionId: "DEC-" + req.approvalId,
        status: { kind, label: label[req.decision] },
        replayed,
      });
    });
  }

  slaSummary() {
    return this.gate(() =>
      ok(zTatSummary, {
        total: 42, avgMinutes: 87.5, p95Minutes: 240, breaches: 5,
        byStatus: [
          { status: "Approved", count: 28, avgMinutes: 65, p95Minutes: 180, breaches: 1 },
          { status: "Rejected", count: 6, avgMinutes: 110, p95Minutes: 260, breaches: 2 },
          { status: "UnderReview", count: 8, avgMinutes: 130, p95Minutes: 300, breaches: 2 },
        ],
      }),
    );
  }
  createManualAuth(input: ManualAuthInput) {
    void input;
    return this.gate(() =>
      ok(zManualAuthResult, { authorizationId: "AUTH-MAN-0007", authNo: "AUTH-2026-0M07", status: { kind: "ok", label: loc("Approved", "معتمد") } }),
    );
  }
  emergencyApprove(authId: string, justification: string) {
    void justification;
    return this.gate(() => ok(zEmergencyResult, { authorizationId: authId, status: { kind: "ok", label: loc("Emergency approved", "اعتماد طارئ") } }));
  }

  directorReport(section: "oversight" | "quality" | "escalations") {
    const views: Record<string, unknown> = {
      oversight: {
        kpis: [
          { label: loc("Pending", "معلّقة"), value: "3" },
          { label: loc("SLA breaches", "تجاوزات"), value: "1" },
          { label: loc("Avg TAT (min)", "متوسط (د)"), value: "58" },
          { label: loc("P95 TAT (min)", "p95 (د)"), value: "120" },
        ],
        tables: [{
          title: loc("Pending by status", "المعلّقة حسب الحالة"),
          columns: [loc("Status", "الحالة"), loc("Priority", "الأولوية"), loc("Count", "العدد")],
          rows: [["Submitted", "Emergency", "1"], ["Submitted", "Urgent", "1"], ["UnderReview", "Routine", "1"]],
        }],
      },
      quality: {
        kpis: [
          { label: loc("Booked", "محجوزة"), value: "60" },
          { label: loc("No-shows", "تخلّف"), value: "9" },
          { label: loc("No-show rate", "نسبة التخلّف"), value: "15%" },
        ],
        tables: [{
          title: loc("Top diagnoses", "أكثر التشخيصات"),
          columns: [loc("ICD-10", "ICD-10"), loc("Count", "العدد")],
          rows: [["E11.9", "23"], ["I10", "19"], ["J06.9", "15"]],
        }],
      },
      escalations: {
        kpis: [{ label: loc("Rejected", "مرفوضة"), value: "2" }],
        tables: [{
          title: loc("Rejections by reason", "الرفض حسب السبب"),
          columns: [loc("Reason", "السبب"), loc("Count", "العدد")],
          rows: [["NOT_COVERED", "1"], ["INSUFFICIENT_DOCS", "1"]],
        }],
      },
    };
    return this.gate(() => ok(zReportView, views[section]));
  }

  // ---- Dashboard ---------------------------------------------------------
  executiveDashboard(scope: "executive" | "finance" | "director") {
    return this.gate(() => {
      const kpis = [
        {
          kind: "kpi" as const,
          id: "tat",
          title: loc("Approval TAT (p95)", "زمن الموافقة (p95)"),
          value: "4.2h",
          delta: "0.6h",
          direction: "down" as const,
          status: { kind: "ok" as const, label: loc("Within SLA", "ضمن المهلة") },
        },
        {
          kind: "kpi" as const,
          id: "pending",
          title: loc("Pending approvals", "موافقات معلّقة"),
          value: "37",
          delta: "5",
          direction: "up" as const,
        },
        {
          kind: "kpi" as const,
          id: "utilization",
          title: loc("Utilization", "معدل الاستخدام"),
          value: "82%",
        },
        {
          kind: "kpi" as const,
          id: "noshow",
          title: loc("No-show rate", "معدل عدم الحضور"),
          value: "6.1%",
          delta: "0.4%",
          direction: "down" as const,
        },
      ];
      const charts = [
        {
          kind: "chart" as const,
          id: "workload",
          title: loc("Clinic workload (visits/day)", "حِمل العيادة (زيارات/يوم)"),
          chartType: "bar" as const,
          series: [
            { label: loc("Mon", "الإثنين"), value: 120, display: "120" },
            { label: loc("Tue", "الثلاثاء"), value: 138, display: "138" },
            { label: loc("Wed", "الأربعاء"), value: 104, display: "104" },
            { label: loc("Thu", "الخميس"), value: 152, display: "152" },
          ],
          dataTable: {
            columns: [loc("Day", "اليوم"), loc("Visits", "زيارات")],
            rows: [["Mon", "120"], ["Tue", "138"], ["Wed", "104"], ["Thu", "152"]],
          },
        },
        // Finance zone: this is a MEDS/spend breakdown, never a diagnosis breakdown (finance ≠ diagnosis).
        {
          kind: "chart" as const,
          id: scope === "finance" ? "spend" : "topdx",
          title:
            scope === "finance"
              ? loc("Top spend categories", "أعلى فئات الإنفاق")
              : loc("Top diagnoses", "أكثر التشخيصات"),
          chartType: "donut" as const,
          series:
            scope === "finance"
              ? [
                  { label: loc("Pharmacy", "صيدلية"), value: 42, display: "42%" },
                  { label: loc("Imaging", "أشعة"), value: 31, display: "31%" },
                  { label: loc("Labs", "مختبر"), value: 27, display: "27%" },
                ]
              : [
                  { label: loc("URTI", "التهاب تنفسي"), value: 40, display: "40%" },
                  { label: loc("Hypertension", "ضغط الدم"), value: 33, display: "33%" },
                  { label: loc("Diabetes", "السكري"), value: 27, display: "27%" },
                ],
          dataTable:
            scope === "finance"
              ? {
                  columns: [loc("Category", "الفئة"), loc("Share", "النسبة")],
                  rows: [["Pharmacy", "42%"], ["Imaging", "31%"], ["Labs", "27%"]],
                }
              : {
                  columns: [loc("Diagnosis", "التشخيص"), loc("Share", "النسبة")],
                  rows: [["URTI", "40%"], ["Hypertension", "33%"], ["Diabetes", "27%"]],
                },
        },
      ];
      return ok(zExecutiveDashboard, {
        version: "1.0",
        generatedAt: NOW,
        scope,
        // Finance dashboards omit any diagnosis breakdown by construction.
        kpis,
        charts: scope === "finance" ? charts.filter((c) => c.id !== "topdx") : charts,
      });
    });
  }

  // ---- Case management (Phase 10.1) — assignment-scoped, coordination summary ----------------------------
  myCases() {
    return this.gate(
      () =>
        ok(z.array(zCaseListItem), [
          {
            id: "CASE-2026-000042",
            caseNo: "CASE-2026-000042",
            beneficiary: { id: "MRS-M-10231", token: "A.H · •••4821" },
            category: "chronic",
            priority: "high",
            status: { kind: "warn", label: loc("Active", "نشطة") },
            openedAt: "2026-07-10T09:00:00Z",
            summary: loc("Diabetes care coordination", "تنسيق رعاية السكري"),
          },
          {
            id: "CASE-2026-000051",
            caseNo: "CASE-2026-000051",
            beneficiary: { id: "MRS-M-10555", token: "Y.H · •••7702" },
            category: "vulnerable",
            priority: "urgent",
            status: { kind: "info", label: loc("Open", "مفتوحة") },
            openedAt: "2026-07-18T11:30:00Z",
            summary: loc("Post-surgery follow-up", "متابعة بعد الجراحة"),
          },
        ]),
      [],
    );
  }
  beneficiary360(caseId: string) {
    return this.gate(() =>
      ok(zBeneficiary360, {
        caseId,
        caseNo: caseId,
        beneficiary: { id: "MRS-M-10231", token: "A.H · •••4821" },
        coverage: {
          status: { kind: "ok", label: loc("Eligible", "مؤهل") },
          planName: loc("Mersal Essential", "مرسال الأساسية"),
          coverageCategory: loc("Band B — Outpatient + Pharmacy", "الفئة ب — عيادات + صيدلية"),
          annualCap: 20000,
          remaining: 8400,
        },
        carePlan: {
          status: loc("Active", "نشطة"),
          goals: [
            loc("HbA1c below 7% by Q4", "خفض السكر التراكمي دون ٧٪ بالربع الرابع"),
            loc("Quarterly retinal screening", "فحص الشبكية الفصلي"),
          ],
          reviewDue: "2026-09-30T00:00:00Z",
        },
        appointments: [
          { id: "APT-2201", clinic: loc("Endocrinology", "الغدد الصماء"), when: "2026-07-28T10:00:00Z", status: { kind: "info", label: loc("Booked", "محجوز") } },
        ],
        openApprovals: [
          { authNo: "AUTH-9001", status: { kind: "info", label: loc("Awaiting review", "بانتظار المراجعة") }, priority: "high", decidedAt: undefined },
        ],
        // Coordination clinical SUMMARY: diagnoses coord-visible; notes/rx/results MASKED (count only).
        clinical: {
          activeDiagnoses: [
            { system: "ICD-10", code: "E11.9", label: loc("Type 2 diabetes", "السكري من النوع ٢") },
            { system: "ICD-10", code: "I10", label: loc("Essential hypertension", "ارتفاع ضغط الدم") },
          ],
          notes: { count: 4, summaryOnly: true },
          prescriptions: { count: 3, summaryOnly: true },
          results: { count: 6, summaryOnly: true },
        },
      }),
    );
  }
  caseTasks(caseId: string) {
    return this.gate(
      () =>
        ok(z.array(zCoordinationTask), [
          { id: "TSK-1", caseId, title: loc("Book retinal screening", "حجز فحص الشبكية"), state: "todo", dueAt: "2026-07-30T00:00:00Z", status: { kind: "info", label: loc("To do", "للتنفيذ") } },
          { id: "TSK-2", caseId, title: loc("Confirm pharmacy refill", "تأكيد صرف الصيدلية"), state: "in_progress", status: { kind: "warn", label: loc("In progress", "قيد التنفيذ") } },
          { id: "TSK-3", caseId, title: loc("Call beneficiary re: diet plan", "الاتصال بالمستفيد بخصوص نظام الغذاء"), state: "done", status: { kind: "ok", label: loc("Done", "تم") } },
        ]),
      [],
    );
  }
  escalations() {
    return this.gate(
      () =>
        ok(z.array(zEscalation), [
          {
            id: "ESC-1",
            caseId: "CASE-2026-000051",
            caseNo: "CASE-2026-000051",
            raisedToRole: loc("Medical Approval", "الموافقة الطبية"),
            reason: "Urgent authorization for post-surgical imaging pending > 24h.",
            status: { kind: "warn", label: loc("Raised", "مُصعّدة") },
            raisedAt: "2026-07-20T08:00:00Z",
          },
        ]),
      [],
    );
  }

  // ---- Finance (Phase 10.2) — billing codes + amounts only, no diagnosis --------------------------------
  utilization() {
    return this.gate(() =>
      ok(zUtilizationView, {
        from: "2026-06-22",
        to: "2026-07-22",
        rows: [
          { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), coverageCategory: loc("Outpatient", "عيادات خارجية"), providerRef: "PRV-•••301", authorizedQty: 12, deliveredQty: 9, spend: 58500 },
          { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), coverageCategory: loc("Pharmacy", "صيدلية"), providerRef: "PRV-•••118", authorizedQty: 240, deliveredQty: 231, spend: 12400 },
          { serviceCode: "80053", serviceLine: loc("Lab", "مختبر"), coverageCategory: loc("Outpatient", "عيادات خارجية"), providerRef: "PRV-•••204", authorizedQty: 88, deliveredQty: 86, spend: 9120 },
        ],
        totalAuthorized: 340,
        totalDelivered: 326,
        totalSpend: 80020,
      }),
    );
  }
  settlements() {
    return this.gate(
      () =>
        ok(z.array(zSettlement), [
          {
            id: "STL-2026-000007",
            settlementNo: "STL-2026-000007",
            providerRef: "PRV-•••301",
            providerName: loc("Nile Imaging Center", "مركز النيل للأشعة"),
            periodStart: "2026-06-01",
            periodEnd: "2026-06-30",
            currency: "EGP",
            total: 58500,
            status: { kind: "info", label: loc("Submitted", "مُقدّمة") },
            state: "submitted",
            lines: [
              { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), deliveredQty: 9, agreedUnitPrice: 6500, lineTotal: 58500 },
            ],
          },
          {
            id: "STL-2026-000006",
            settlementNo: "STL-2026-000006",
            providerRef: "PRV-•••118",
            providerName: loc("Cairo Community Pharmacy", "صيدلية القاهرة") ,
            periodStart: "2026-06-01",
            periodEnd: "2026-06-30",
            currency: "EGP",
            total: 12400,
            status: { kind: "ok", label: loc("Approved", "معتمدة") },
            state: "approved",
            lines: [
              { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), deliveredQty: 231, agreedUnitPrice: 53.68, lineTotal: 12400 },
            ],
          },
        ]),
      [],
    );
  }
  financialSummary(dimension: "serviceline" | "category" | "provider") {
    return this.gate(() =>
      ok(zFinancialSummary, {
        dimension,
        buckets: [
          { key: loc("Imaging", "أشعة"), deliveredQty: 9, spend: 58500, sharePercent: 73 },
          { key: loc("Pharmacy", "صيدلية"), deliveredQty: 231, spend: 12400, sharePercent: 15 },
          { key: loc("Lab", "مختبر"), deliveredQty: 86, spend: 9120, sharePercent: 12 },
        ],
        totalSpend: 80020,
      }),
    );
  }
  exportReport(req: ExportRequest) {
    return this.gate(() =>
      ok(zExportResult, {
        report: req.report,
        format: req.format,
        rowCount: 3,
        filename: `${req.report}-${req.from}_${req.to}.${req.format}`,
        status: { kind: "ok", label: loc("Export ready (audited)", "التصدير جاهز (مُدقّق)") },
      }),
    );
  }

  // ---- Claims management (Phase 10b) — codes + amounts only, no diagnosis (finance-parity) --------------
  claimsWorklist(status?: string) {
    const rows = [
      { id: "clm-1", claimNo: "CLM-2026-004411", origin: "Provider", key: "Submitted", st: { kind: "info" as const, label: loc("Submitted", "مُقدّمة") }, claimed: 3200, net: null, from: "2026-07-18", at: "2026-07-19T09:00:00Z" },
      { id: "clm-2", claimNo: "CLM-2026-004412", origin: "AutoDerived", key: "Adjudicated", st: { kind: "ok" as const, label: loc("Adjudicated", "تمت المراجعة") }, claimed: 1450, net: 1305, from: "2026-07-15", at: "2026-07-16T11:30:00Z" },
      { id: "clm-3", claimNo: "CLM-2026-004413", origin: "Reimbursement", key: "Rejected", st: { kind: "bad" as const, label: loc("Rejected", "مرفوضة") }, claimed: 900, net: 0, from: "2026-07-12", at: "2026-07-13T08:15:00Z" },
    ].filter((r) => !status || r.key === status);
    return this.gate(
      () =>
        ok(z.array(zClaimRow), rows.map((r) => ({
          id: r.id, claimNo: r.claimNo, origin: r.origin, status: r.st, currency: "EGP",
          claimedAmount: r.claimed, netPayable: r.net, serviceDateFrom: r.from, submittedAt: r.at,
        }))),
      [],
    );
  }

  claimsReconciliation(bucket?: string) {
    const rows = [
      { claimId: "clm-1", claimNo: "CLM-2026-004411", origin: "Provider", code: "80053", date: "2026-07-18", billed: 320, allowed: 300, bucket: "PriceVariance", st: { kind: "warn" as const, label: loc("Price variance", "فرق سعر") } },
      { claimId: "clm-4", claimNo: "CLM-2026-004420", origin: "AutoDerived", code: "71046", date: "2026-07-17", billed: 800, allowed: null, bucket: "BilledNotDelivered", st: { kind: "bad" as const, label: loc("Billed, not delivered", "فوترة بلا تنفيذ") } },
      { claimId: "clm-5", claimNo: "CLM-2026-004421", origin: "Provider", code: "85025", date: "2026-07-16", billed: 150, allowed: 150, bucket: "Matched", st: { kind: "ok" as const, label: loc("Matched", "مطابقة") } },
    ].filter((r) => !bucket || r.bucket === bucket);
    return this.gate(
      () =>
        ok(z.array(zReconciliationRow), rows.map((r) => ({
          claimId: r.claimId, claimNo: r.claimNo, origin: r.origin, code: r.code, serviceDate: r.date,
          billedAmount: r.billed, allowedAmount: r.allowed, bucket: r.bucket, status: r.st,
        }))),
      [],
    );
  }

  claimsKpis() {
    return this.gate(() =>
      ok(zClaimsKpis, {
        averageTatHours: 34.5, approvalRate: 0.82, denialRate: 0.11, ocrAutoMatchRate: 0.74,
        agedUnbilledCount: 12, agedUnbilledValue: 41250, recoveryOutstanding: 8900,
        topDenialReasons: [
          { reason: "Missing authorization", count: 7 },
          { reason: "Non-covered service", count: 4 },
          { reason: "Duplicate claim", count: 3 },
        ],
      }),
    );
  }

  // ---- Notifications (Phase 8.1) — the caller's own in-app inbox, cross-portal --------------------------
  notifications(unreadOnly?: boolean) {
    // Reads that happened in this session are overlaid on the fixture, so a screen that reloads after
    // marking read sees the write — the same way it would against the service.
    const rows = NOTIFICATION_FIXTURE.map((n) =>
      this.notificationsRead.has(n.id) ? { ...n, read: true } : n,
    );
    return this.gate(
      () => ok(z.array(zNotification), unreadOnly ? rows.filter((n) => !n.read) : rows),
      [],
    );
  }
  private unreadNotificationIds() {
    return NOTIFICATION_FIXTURE.filter((n) => !n.read && !this.notificationsRead.has(n.id)).map((n) => n.id);
  }
  markNotificationRead(id: string) {
    this.notificationsRead.add(id);
    return this.gate(() => ok(zMarkReadResult, { id, read: true }));
  }
  markAllNotificationsRead() {
    const marked = this.unreadNotificationIds();
    marked.forEach((id) => this.notificationsRead.add(id));
    return this.gate(() => ok(zMarkAllReadResult, { marked: marked.length }));
  }

  // ---- Admin / platform governance (Phase 8b) — WHO can access, not content ------------------------------
  accessMatrix() {
    return this.gate(
      () =>
        ok(z.array(zRoleBinding), [
          { id: "RB-1", subjectToken: "•••8a91", role: "doctor", scope: "Tenant", tier: "T4", status: { kind: "ok", label: loc("Active", "نشط") }, grantedAt: "2026-06-25T09:00:00Z", reviewDueAt: "2026-09-25T09:00:00Z" },
          { id: "RB-2", subjectToken: "•••1c07", role: "finance", scope: "Tenant", tier: "T2", status: { kind: "ok", label: loc("Active", "نشط") }, grantedAt: "2026-06-25T09:00:00Z" },
        ]),
      [],
    );
  }
  adminTenants() {
    return this.gate(
      () => ok(z.array(zTenantSummary), [{ id: "T-1", name: "Mersal Foundation", status: { kind: "ok", label: loc("Active", "نشط") }, createdAt: "2026-01-01T00:00:00Z" }]),
      [],
    );
  }
  sodMatrix() {
    return this.gate(
      () =>
        ok(z.array(zSodConflict), [
          { roleA: "doctor", roleB: "medical_approval", reason: "Self-approval of own clinical request" },
          { roleA: "finance", roleB: "finance", reason: "Initiator must not release own payment" },
        ]),
      [],
    );
  }
  accessReviewCampaigns() {
    return this.gate(
      () => ok(z.array(zAccessReviewCampaign), [{ id: "CAMP-1", name: "Q3 2026 high-sensitivity access recertification", status: { kind: "info", label: loc("Open", "مفتوحة") }, minTier: "T3", dueAt: "2026-08-05T00:00:00Z" }]),
      [],
    );
  }
  breakGlassGrants() {
    return this.gate(
      () => ok(z.array(zBreakGlassGrant), [{ id: "BG-1", requesterToken: "•••8a91", reasonCode: "EmergencyCare", status: { kind: "neu", label: loc("Expired", "منتهٍ") }, requestedAt: "2026-07-20T02:00:00Z", expiresAt: "2026-07-20T03:00:00Z" }]),
      [],
    );
  }
  providerList() {
    return this.gate(
      () => ok(z.array(zProviderSummary), [
        { id: "PRV-1", code: "PRV-0001", legalName: "Nile Central Hospital", providerType: "Hospital", status: { kind: "ok", label: loc("Active", "نشط") }, onboardingState: "Activated" },
        { id: "PRV-2", code: "PRV-0002", legalName: "Cairo Care Clinic", providerType: "Clinic", status: { kind: "ok", label: loc("Active", "نشط") }, onboardingState: "Activated" },
        { id: "PRV-3", code: "PRV-0003", legalName: "Delta Diagnostics Lab", providerType: "Lab", status: { kind: "warn", label: loc("Suspended", "موقوف") }, onboardingState: "Credentialed" },
      ]),
      [],
    );
  }
  providerLocations(providerId: string) {
    void providerId;
    return this.gate(
      () => ok(z.array(zProviderLocation), [
        { id: "LOC-1", name: "Main Campus", governorate: "Cairo", address: "12 Nile Corniche", isPrimary: true },
        { id: "LOC-2", name: "East Annex", governorate: "Cairo", address: "4 Salah Salem St", isPrimary: false },
      ]),
      [],
    );
  }
  providerContracts(providerId: string) {
    void providerId;
    return this.gate(
      () => ok(z.array(zProviderContract), [
        { id: "CON-1", contractNo: "CON-2026-0001", status: { kind: "ok", label: loc("Active", "نشط") }, effectiveFrom: "2026-01-01", effectiveTo: "2026-12-31", serviceLines: 4 },
      ]),
      [],
    );
  }
  createProvider(input: CreateProviderInput) {
    return this.gate(() => ok(zProviderSummary, { id: "PRV-NEW", code: input.code, legalName: input.legalName, providerType: input.providerType, status: { kind: "warn", label: loc("Suspended", "موقوف") }, onboardingState: "Draft" }));
  }

  // ---- Patient profile (Phase 20, design 39) --------------------------------------------------------------
  // The fixture deliberately carries ALL FOUR states at once — Visible, Restricted, Unavailable and
  // NotApplicable — because the three non-visible ones are the part of this screen most likely to be got
  // wrong, and a fixture that only shows happy-path sections is a fixture in which "restricted" and "broken"
  // and "empty" never get looked at side by side.
  patientProfile(beneficiaryId: string, sections?: ProfileSectionKey[]) {
    const all = [
      {
        key: "header", state: "Visible" as const,
        data: {
          beneficiaryId, memberNo: "MRS-M-014882", displayName: "Amal Hassan", displayNameAr: "أمل حسن",
          ageBand: "30-39", sex: "F", status: "Active",
          statusCue: { label: "Active", icon: "check-circle", shape: "circle", tone: "positive" },
          branchName: "Nasr City", preferredLanguage: "ar",
          contact: { phone: "+20 100 000 0000", preferredChannel: "WhatsApp" },
          photoUrl: `/api/v1/patients/${beneficiaryId}/photo`,
        },
      },
      {
        key: "alerts", state: "Visible" as const,
        data: {
          allergies: [{ allergen: "Penicillin", reaction: "Rash", severity: "High" }],
          criticalFlags: [{ kind: "Critical", label: "Anticoagulated", tone: "critical" }],
        },
      },
      {
        key: "coverage", state: "Visible" as const,
        data: {
          payerName: "Mersal Foundation", policyNo: "POL-2026-0001", planLabel: "Gold", planVersion: 3,
          effectiveFrom: "2026-01-01", effectiveTo: "2026-12-31", waitingPeriodState: "Served",
          categories: [
            { category: "Pharmacy", annualLimit: 5000, consumed: 1200, remaining: 3800, costSharePercent: 10, costShareTier: "Tier1" },
            { category: "Dental", annualLimit: 2000, consumed: 0, remaining: 2000, costSharePercent: 20, costShareTier: "Tier2" },
          ],
        },
      },
      // Restricted: the locked state, with the reason AND the way out.
      {
        key: "investigations", state: "Restricted" as const, reasonCode: "sensitive-requires-grant",
        requestAccessAction: { kind: "report-access-request", href: `/api/v1/report-access-requests?beneficiaryId=${beneficiaryId}`, label: "Request access" },
      },
      // Unavailable: the owning service did not answer. NOT the same as empty — the user gets Retry.
      { key: "encounters", state: "Unavailable" as const, reasonCode: "timeout" },
      // NotApplicable: nothing exists. A plain, calm "no records".
      { key: "referrals", state: "NotApplicable" as const },
      {
        key: "callHistory", state: "Visible" as const,
        data: {
          level: "Full",
          items: [
            {
              callRef: "CALL-2026-004137", direction: "Outbound", startedAt: "2026-07-24T12:32:00Z",
              endedAt: "2026-07-24T12:38:12Z", durationSeconds: 372, branchCode: "Nasr City",
              agentDisplayName: "R. Adel", reasonCode: "RescheduleAppointment", outcome: "Resolved",
              summary: "Appointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request; member confirmed the new slot on the call.",
              summaryEdited: false,
              linkedArtifacts: [{ type: "Appointment", ref: "APT-2026-8841", action: "Reschedule" }],
              copyText: "[Outbound] 2026-07-24 15:32 (6m 12s) · Nasr City · Agent: R. Adel\nMember: MRS-M-014882 · Ref: CALL-2026-004137\nReason: RescheduleAppointment · Outcome: Resolved\nAppointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request; member confirmed the new slot on the call.",
            },
            {
              callRef: "CALL-2026-004102", direction: "Inbound", startedAt: "2026-07-11T08:05:00Z",
              endedAt: "2026-07-11T08:07:40Z", durationSeconds: 160, branchCode: "Nasr City",
              agentDisplayName: "M. Farid", reasonCode: "EligibilityEnquiry", outcome: "Resolved",
              summary: "Member asked whether dental is covered this year; confirmed remaining dental limit and how to book.",
              summaryEdited: true, linkedArtifacts: [],
              copyText: "[Inbound] 2026-07-11 11:05 (2m 40s) · Nasr City · Agent: M. Farid\nMember: MRS-M-014882 · Ref: CALL-2026-004102\nReason: EligibilityEnquiry · Outcome: Resolved\nMember asked whether dental is covered this year; confirmed remaining dental limit and how to book.",
            },
          ],
        },
      },
    ];

    const wanted = sections?.length ? new Set<string>(sections) : null;
    return this.gate(() =>
      ok(zPatientProfile, {
        beneficiaryId,
        servedAt: new Date().toISOString(),
        sections: all.filter((s) => !wanted || wanted.has(s.key)),
      }),
    );
  }

  profileSummary(beneficiaryId: string) {
    return this.gate(() =>
      ok(zProfileExportSummary, {
        profile: {
          beneficiaryId,
          servedAt: new Date().toISOString(),
          sections: [{ key: "header", state: "Visible" as const, data: { beneficiaryId, displayName: "Amal Hassan", status: "Active", statusCue: { label: "Active", icon: "check-circle", shape: "circle", tone: "positive" } } }],
        },
        watermark: {
          viewerSubject: "dev-user",
          viewerRoles: "doctor",
          generatedAt: new Date().toISOString(),
          purpose: "profile-export",
        },
      }),
    );
  }

  copyCallSummaries(beneficiaryId: string, callRefs: string[]) {
    void beneficiaryId;
    return this.gate(() =>
      ok(zCopySummariesResult, {
        level: "Full",
        callRefs,
        copyText: callRefs
          .map((r) => `[Outbound] 2026-07-24 15:32 · Ref: ${r}`)
          .join("\n\n"),
      }),
    );
  }

  beneficiarySearch(query: { name?: string; status?: string }) {
    const all = [
      { id: "BEN-1", memberNo: "MRS-M-10231", givenName: "Omar", familyName: "Khaled", chip: { kind: "info" as const, label: loc("Pending", "قيد الانتظار") }, raw: "Pending", ids: [{ type: "NationalID", value: "•••2931", isPrimary: true }] },
      { id: "BEN-2", memberNo: "MRS-M-10555", givenName: "Salma", familyName: "Adel", chip: { kind: "ok" as const, label: loc("Active", "نشط") }, raw: "Active", ids: [{ type: "UNHCRNo", value: "801-•••45", isPrimary: true }] },
      { id: "BEN-3", memberNo: undefined, givenName: "Amina", familyName: "Yusuf", chip: { kind: "warn" as const, label: loc("Suspended", "موقوف") }, raw: "Suspended", ids: [{ type: "Passport", value: "A•••221", isPrimary: true }] },
      // The awkward states are the ones the status screen exists to make legible: a fraud-Blocked record
      // the desk must NOT be able to touch, and an Expired one whose only edge is renewal.
      { id: "BEN-4", memberNo: "MRS-M-10102", givenName: "Hassan", familyName: "Tariq", chip: { kind: "bad" as const, label: loc("Blocked", "محظور") }, raw: "Blocked", ids: [{ type: "UNHCRNo", value: "802-•••71", isPrimary: true }] },
      { id: "BEN-5", memberNo: "MRS-M-10077", givenName: "Layla", familyName: "Nasser", chip: { kind: "neu" as const, label: loc("Expired", "منتهٍ") }, raw: "Expired", ids: [{ type: "NationalID", value: "•••8843", isPrimary: true }] },
    ].filter((b) => (!query.name || (b.givenName + " " + b.familyName).toLowerCase().includes(query.name.toLowerCase())) && (!query.status || b.raw === query.status));
    return this.gate(
      () => ok(z.array(zBeneficiaryRow), all.map((b) => ({
        id: b.id, memberNo: b.memberNo, givenName: b.givenName, familyName: b.familyName,
        status: b.chip, statusRaw: b.raw, identifiers: b.ids,
      }))),
      [],
    );
  }
  registerBeneficiary(input: RegisterBeneficiaryInput) {
    void input;
    return this.gate(() => ok(zRegisterResult, { id: "BEN-NEW", memberNo: undefined, status: { kind: "info", label: loc("Pending", "قيد الانتظار") } }));
  }
  changeBeneficiaryStatus(id: string, toStatus: string, reason: string) {
    void reason;
    return this.gate(() => ok(zStatusChangeResult, { id, status: { kind: toStatus === "Active" ? "ok" : "warn", label: loc(toStatus, toStatus) } }));
  }

  // Registration approval worklist (US-003). The three shapes the screen must make legible: an application
  // mid-preparation, one bounced back for more information, and a legacy beneficiary with no application.
  registrationWorklist() {
    return this.gate(
      () =>
        ok(z.array(zRegistrationWorkItem), [
          {
            beneficiary: {
              id: "BEN-1", memberNo: undefined, givenName: "Omar", familyName: "Khaled",
              status: { kind: "info", label: loc("Pending", "قيد الانتظار") }, statusRaw: "Pending",
              identifiers: [{ type: "NationalID", value: "•••2931", isPrimary: true }],
            },
            registration: { id: "REG-1", status: "Pending", documentsVerified: true, coverageBound: false, notes: null },
          },
          {
            beneficiary: {
              id: "BEN-6", memberNo: undefined, givenName: "Rania", familyName: "Mostafa",
              status: { kind: "info", label: loc("Pending", "قيد الانتظار") }, statusRaw: "Pending",
              identifiers: [{ type: "RefugeeID", value: "R•••501", isPrimary: true }],
            },
            registration: { id: "REG-2", status: "InfoRequested", documentsVerified: false, coverageBound: false, notes: "UNHCR letter is expired — request a current one" },
          },
          {
            beneficiary: {
              id: "BEN-7", memberNo: undefined, givenName: "Karim", familyName: "Fawzy",
              status: { kind: "info", label: loc("Pending", "قيد الانتظار") }, statusRaw: "Pending",
              identifiers: [{ type: "UNHCRNo", value: "803-•••12", isPrimary: true }],
            },
            registration: null,
          },
        ]),
      [],
    );
  }
  createRegistration() {
    return this.gate(() => undefined);
  }
  setRegistrationChecks() {
    return this.gate(() => undefined);
  }
  decideRegistration(_id: string, decision: "Approve" | "RequestInfo" | "Reject") {
    return this.gate(() =>
      ok(zRegistrationDecisionResult, decision === "Approve"
        ? { status: "Active", memberNo: "MRS-M-2026-000418" }
        : { status: decision === "Reject" ? "Rejected" : "InfoRequested" }),
    );
  }

  adminMasterData() {
    return this.gate(
      () => ok(z.array(zMasterDataVersion), [
        { id: "MDV-1", system: "ICD10", code: "E11.9", versionNo: 2, retired: false, effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Annual ICD refresh" },
        { id: "MDV-2", system: "ATC", code: "A10BA02", versionNo: 1, retired: false, effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Initial load" },
      ]),
      [],
    );
  }
  adminSystemConfig() {
    return this.gate(
      () => ok(z.array(zSystemConfigEntry), [
        { id: "CFG-1", tenantId: "*", key: "session.timeout_minutes", type: "Duration", value: "15", versionNo: 1 },
        { id: "CFG-2", tenantId: "11111111-1111-1111-1111-111111111111", key: "approvals.sla_hours", type: "Whole", value: "24", versionNo: 3 },
      ]),
      [],
    );
  }

  // ---- User & access model (Phase 21.6, design 40) -------------------------------------------------------
  //
  // The fixtures deliberately include the awkward states, because those are the ones the screens exist to
  // make legible: a suspended membership, a lapsed override, an open-ended branch grant, a cap already
  // exceeded, and a feature nobody has configured either way.

  memberships() {
    return this.gate(() => ok(z.array(zMembershipRow), DEV_MEMBERSHIPS), []);
  }

  membership(membershipId: string) {
    const row = DEV_MEMBERSHIPS.find((m) => m.membershipId === membershipId) ?? DEV_MEMBERSHIPS[0];
    return this.gate(() =>
      ok(zMembershipDetail, {
        ...row,
        providerId: null,
        homeBranchId: "b1000000-0000-0000-0000-000000000001",
        overrides: [
          {
            id: "OV-1", scope: "orders:read", effect: "Deny", reason: "Under investigation — access narrowed pending review",
            grantedBy: "admin@mersal", validUntil: null, expired: false,
          },
          {
            id: "OV-2", scope: "reports:export", effect: "Allow", reason: "Covering the monthly extract while N. is on leave",
            grantedBy: "admin@mersal", validUntil: "2026-08-31T00:00:00Z", expired: false,
          },
          // Lapsed on purpose: the screen must show it as expired rather than hide it, so an administrator
          // can explain why this person lost the key overnight.
          {
            id: "OV-3", scope: "claims:submit", effect: "Allow", reason: "Ramadan surge cover",
            grantedBy: "admin@mersal", validUntil: "2026-04-30T00:00:00Z", expired: true,
          },
        ],
      }),
    );
  }

  setMembershipOverride() {
    return this.gate(() => undefined);
  }

  effectiveAccess(membershipId: string) {
    return this.gate(() =>
      ok(zEffectiveAccess, {
        membershipId,
        keys: [
          { key: "encounters:read", source: "role", via: "doctor" },
          { key: "prescriptions:write", source: "role", via: "doctor" },
          { key: "reports:export", source: "override", via: "admin@mersal", reason: "Covering the monthly extract while N. is on leave" },
          { key: "labs:read", source: "role", via: "doctor", deprecated: true, replacedBy: "investigations:read" },
          { key: "orders:read", source: "denied", via: "admin@mersal", reason: "Under investigation — access narrowed pending review" },
        ],
      }),
    );
  }

  branchScopeGrants() {
    return this.gate(
      () =>
        ok(z.array(zBranchScopeGrant), [
          {
            grantId: "G-1", branchId: "b1000000-0000-0000-0000-000000000001", isHome: true,
            validFrom: "2026-01-01", validUntil: null, grantedBy: "admin@mersal", grantedReason: "Home branch",
          },
          {
            grantId: "G-2", branchId: "b1000000-0000-0000-0000-000000000002", isHome: false,
            validFrom: "2026-10-01", validUntil: "2026-10-31", grantedBy: "admin@mersal",
            grantedReason: "Covering Alexandria for October",
          },
        ]),
      [],
    );
  }

  accessSessions() {
    return this.gate(
      () =>
        ok(z.array(zAccessSession), [
          { sessionId: "S-1", device: "Chrome on Windows", createdAt: "2026-07-28T06:10:00Z", lastSeenAt: "2026-07-28T09:31:00Z", current: true },
          { sessionId: "S-2", device: "Safari on iPhone", createdAt: "2026-07-20T18:02:00Z", lastSeenAt: "2026-07-26T07:44:00Z", current: false },
        ]),
      [],
    );
  }

  revokeAccessSession() {
    return this.gate(() => undefined);
  }

  programEnablement(tenant: string) {
    return this.gate(() =>
      ok(zProgramEnablement, {
        tenantId: tenant,
        features: [
          { key: "approvals", enabled: true, configured: true, changedBy: "programme@mersal", changedAt: "2026-05-01T09:00:00Z" },
          { key: "callcentre", enabled: true, configured: true, changedBy: "programme@mersal", changedAt: "2026-06-11T09:00:00Z" },
          { key: "claims", enabled: false, configured: true, changedBy: "programme@mersal", changedAt: "2026-02-14T09:00:00Z" },
          // Never configured either way — shown as its own state, not folded into "off".
          { key: "interop", enabled: false, configured: false, changedBy: null, changedAt: null },
        ],
        limits: [
          { key: "active_users", maxValue: 50, currentUsage: 42, changedBy: "programme@mersal", changedAt: "2026-05-01T09:00:00Z" },
          // Already over its cap: legitimate after a cap is tightened, and the screen must say so plainly
          // rather than render a bar that silently overflows.
          { key: "active_provider_users", maxValue: 10, currentUsage: 12, changedBy: "programme@mersal", changedAt: "2026-07-01T09:00:00Z" },
          { key: "monthly_extracts", maxValue: 20, currentUsage: null, changedBy: null, changedAt: null },
          { key: "storage_mb", maxValue: null, currentUsage: null, changedBy: null, changedAt: null },
        ],
      }),
    );
  }

  setProgramFeature() {
    return this.gate(() => undefined);
  }

  setProgramLimit() {
    return this.gate(() => undefined);
  }
}

/** One identity holding two memberships with different authority — invariant 1, made visible. */
const DEV_MEMBERSHIPS = [
  {
    membershipId: "11111111-1111-1111-1111-111111111111",
    userId: "aaaaaaaa-1111-1111-1111-111111111111",
    username: "s.ibrahim", displayName: "Sara Ibrahim",
    tenantId: "mersal", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "doctor", level: 3 }], level: 3, isPlatformAdmin: false,
    overrideCount: 3, expiredOverrideCount: 1,
    activatedAt: "2026-01-15T08:00:00Z", endedAt: null,
  },
  {
    // Same person, different organisation, genuinely different authority — never a blended principal.
    membershipId: "22222222-2222-2222-2222-222222222222",
    userId: "aaaaaaaa-1111-1111-1111-111111111111",
    username: "s.ibrahim", displayName: "Sara Ibrahim",
    tenantId: "partner-ngo", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "provider_admin", level: 2 }], level: 2, isPlatformAdmin: false,
    overrideCount: 0, expiredOverrideCount: 0,
    activatedAt: "2026-03-02T08:00:00Z", endedAt: null,
  },
  {
    membershipId: "33333333-3333-3333-3333-333333333333",
    userId: "bbbbbbbb-2222-2222-2222-222222222222",
    username: "m.farouk", displayName: "Mohamed Farouk",
    tenantId: "mersal", status: { kind: "warn" as const, label: loc("Suspended", "موقوفة") },
    roles: [{ name: "pharmacist", level: 4 }], level: 4, isPlatformAdmin: false,
    overrideCount: 0, expiredOverrideCount: 0,
    activatedAt: "2026-02-01T08:00:00Z", endedAt: null,
  },
];
