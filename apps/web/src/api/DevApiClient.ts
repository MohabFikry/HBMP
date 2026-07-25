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
  zCaseListItem,
  zCoordinationTask,
  zEscalation,
  zNotification,
  zMarkReadResult,
  zRoleBinding,
  zTenantSummary,
  zSodConflict,
  zAccessReviewCampaign,
  zAppointmentRow,
  zBreakGlassGrant,
  zMasterDataVersion,
  zSystemConfigEntry,
  zProviderSummary,
  zProviderLocation,
  zProviderContract,
  type CreateProviderInput,
  zCheckInResult,
  zOrderRow,
  zRxRow,
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
} from "@mersal/contracts";
import type { ApiClient, ApiScenario } from "./client";
import { ApiError } from "./http";

const loc = (en: string, ar: string): Localized => ({ en, ar });
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
          annualCapRemaining: "EGP 8,400",
        },
        visitGate: { allowed: true },
      }),
    );
  }

  // ---- Reception day board -----------------------------------------------
  appointments(filter: "all" | "booked" | "checked-in" = "all") {
    const rows = [
      { id: "appt-1", token: "•••4821", type: "Consultation", ar: "كشف", st: "Booked", chip: { kind: "info" as const, label: loc("Booked", "محجوز") }, at: "2026-07-22T09:00:00Z", eligible: true },
      { id: "appt-2", token: "•••7710", type: "FollowUp", ar: "متابعة", st: "CheckedIn", chip: { kind: "ok" as const, label: loc("Checked in", "تم الوصول") }, at: "2026-07-22T09:30:00Z", eligible: false },
      { id: "appt-3", token: "•••2093", type: "Consultation", ar: "كشف", st: "Booked", chip: { kind: "info" as const, label: loc("Booked", "محجوز") }, at: "2026-07-22T10:00:00Z", eligible: true },
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
        }))),
      [],
    );
  }
  checkIn(appointmentId: string) {
    return this.gate(() => ok(zCheckInResult, { id: appointmentId, status: { kind: "ok", label: loc("Checked in", "تم الوصول") } }));
  }

  // ---- EMR ---------------------------------------------------------------
  listPatients() {
    return this.gate(
      () =>
        ok(z.array(zPatientListItem), [
          {
            id: "MRS-M-10231",
            name: loc("Amal Hassan", "أمل حسن"),
            mrn: "MRN-10231",
            treating: true,
            lastVisit: "2026-07-01",
            status: { kind: "ok", label: loc("In consultation", "في الكشف") },
          },
          {
            id: "MRS-M-10555",
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
      { id: "ord-1", no: "ORD-2026-000118", tok: "•••4821", type: "Lab", code: "80053", n: 1, st: { kind: "info" as const, label: loc("Active", "نشط") }, key: "Active", at: "2026-07-22T08:10:00Z" },
      { id: "ord-2", no: "ORD-2026-000119", tok: "•••7710", type: "Imaging", code: "71046", n: 1, st: { kind: "ok" as const, label: loc("Completed", "مكتمل") }, key: "Completed", at: "2026-07-21T14:00:00Z" },
      { id: "ord-3", no: "ORD-2026-000120", tok: "•••2093", type: "Lab", code: "85025", n: 2, st: { kind: "ok" as const, label: loc("Completed", "مكتمل") }, key: "Completed", at: "2026-07-20T09:30:00Z" },
    ].filter((r) => !status || r.key === status);
    return this.gate(
      () =>
        ok(z.array(zOrderRow), rows.map((r) => ({
          id: r.id, orderNo: r.no, beneficiary: { id: r.id, token: r.tok },
          orderType: r.type, primaryCode: r.code, lineCount: r.n, status: r.st, requestedAt: r.at,
        }))),
      [],
    );
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
          annualCap: "EGP 20,000",
          remaining: "EGP 8,400",
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
          { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), coverageCategory: loc("Outpatient", "عيادات خارجية"), providerRef: "PRV-•••301", authorizedQty: 12, deliveredQty: 9, spend: "EGP 58,500" },
          { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), coverageCategory: loc("Pharmacy", "صيدلية"), providerRef: "PRV-•••118", authorizedQty: 240, deliveredQty: 231, spend: "EGP 12,400" },
          { serviceCode: "80053", serviceLine: loc("Lab", "مختبر"), coverageCategory: loc("Outpatient", "عيادات خارجية"), providerRef: "PRV-•••204", authorizedQty: 88, deliveredQty: 86, spend: "EGP 9,120" },
        ],
        totalAuthorized: 340,
        totalDelivered: 326,
        totalSpend: "EGP 80,020",
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
            total: "EGP 58,500",
            status: { kind: "info", label: loc("Submitted", "مُقدّمة") },
            state: "submitted",
            lines: [
              { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), deliveredQty: 9, agreedUnitPrice: "EGP 6,500", lineTotal: "EGP 58,500" },
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
            total: "EGP 12,400",
            status: { kind: "ok", label: loc("Approved", "معتمدة") },
            state: "approved",
            lines: [
              { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), deliveredQty: 231, agreedUnitPrice: "EGP 53.68", lineTotal: "EGP 12,400" },
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
          { key: loc("Imaging", "أشعة"), deliveredQty: 9, spend: "EGP 58,500", sharePercent: 73 },
          { key: loc("Pharmacy", "صيدلية"), deliveredQty: 231, spend: "EGP 12,400", sharePercent: 15 },
          { key: loc("Lab", "مختبر"), deliveredQty: 86, spend: "EGP 9,120", sharePercent: 12 },
        ],
        totalSpend: "EGP 80,020",
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

  // ---- Notifications (Phase 8.1) — the caller's own in-app inbox, cross-portal --------------------------
  notifications(unreadOnly?: boolean) {
    const all = [
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
    return this.gate(
      () => ok(z.array(zNotification), unreadOnly ? all.filter((n) => !n.read) : all),
      [],
    );
  }
  markNotificationRead(id: string) {
    return this.gate(() => ok(zMarkReadResult, { id, read: true }));
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
}
