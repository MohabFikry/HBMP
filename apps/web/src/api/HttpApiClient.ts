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
  zKpiWidget,
  zChartWidget,
  zLabOrder,
  zPatientListItem,
  zPlaceOrderResult,
  zPrescribeResult,
  zPrescription,
  zBeneficiary360,
  zCaseListItem,
  zCoordinationTask,
  zEscalation,
  zExportResult,
  zFinancialSummary,
  zSettlement,
  zUtilizationView,
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type ExportRequest,
  type PlaceOrderRequest,
  type PrescribeRequest,
} from "@mersal/contracts";
import type { ApiClient } from "./client";
import { getJson, postJson, getRaw, postRaw, parseOr } from "./http";

/** Wrap a plain service string as the bilingual shape the portal contracts use (same text both langs). */
const loc = (s: unknown) => ({ en: String(s ?? ""), ar: String(s ?? "") });
/** Pre-format a numeric amount as the contract's display string, e.g. 12400 -> "EGP 12,400". */
const money = (n: unknown) => `EGP ${Number(n ?? 0).toLocaleString("en-US", { maximumFractionDigits: 0 })}`;
/** Map a service case status (Open/Active/OnHold/Resolved/Closed) to the contract's snake_case enum. */
const caseStatus = (s: unknown) =>
  ({ open: "open", active: "active", onhold: "on_hold", resolved: "resolved", closed: "closed" })[
    String(s ?? "open").toLowerCase()
  ] ?? "open";
/** A masked, min-necessary display token for a case row (never a beneficiary name). */
const caseToken = (c: any) => `•••${String(c.beneficiaryId ?? c.caseId ?? "").slice(-4)}`;
/* eslint-disable @typescript-eslint/no-explicit-any */

/** Map the reception card's accessible status tone to the design-system StatusKind. */
const toneToKind = (tone: unknown): "ok" | "warn" | "bad" | "neu" | "info" =>
  ({ positive: "ok", caution: "warn", critical: "bad", neutral: "neu" })[String(tone ?? "neutral")] as any ?? "neu";
/** Map member coverage status → the eligibility verdict the result card renders. */
const statusToVerdict = (status: unknown): "eligible" | "ineligible" | "review" => {
  const s = String(status ?? "").toLowerCase();
  if (s === "active") return "eligible";
  if (s === "blocked" || s === "expired") return "ineligible";
  return "review";
};
/** Map an emr encounter status → a resolved bilingual StatusKind for the doctor worklist chip. */
const encounterStatus = (s: unknown) => {
  const k = String(s ?? "InProgress");
  const map: Record<string, { kind: "ok" | "info" | "neu"; label: { en: string; ar: string } }> = {
    InProgress: { kind: "info", label: { en: "In progress", ar: "جارٍ" } },
    Completed: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } },
  };
  return map[k] ?? map.InProgress;
};
/** Map an orders CodeSystem to the zCoded system enum (LOCAL has no clinical code space → fall back to LOINC). */
const codeSystem = (s: unknown): "CPT" | "LOINC" | "ICD-10" | "ATC" | "RxNorm" =>
  ({ CPT: "CPT", LOINC: "LOINC", LOCAL: "LOINC" })[String(s ?? "LOINC")] as any ?? "LOINC";
/** Map an orders/order-line status → a resolved bilingual StatusKind for the fulfillment queue. */
const orderStatus = (s: unknown) => {
  const k = String(s ?? "Active");
  const map: Record<string, { kind: "ok" | "info" | "part" | "neu"; label: { en: string; ar: string } }> = {
    Active: { kind: "info", label: { en: "Active", ar: "نشط" } },
    PartiallyUsed: { kind: "part", label: { en: "Partially used", ar: "مُستخدم جزئياً" } },
    Completed: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } },
  };
  return map[k] ?? map.Active;
};
/** orderId → first available order-line id, cached from the queue so consume can target a concrete line. */
const orderLineByOrderId = new Map<string, string>();
/** Map a pharmacy prescription/line status → a resolved bilingual StatusKind for the dispensing queue. */
const rxStatus = (s: unknown) => {
  const k = String(s ?? "Approved");
  const map: Record<string, { kind: "ok" | "info" | "part" | "neu"; label: { en: string; ar: string } }> = {
    Approved: { kind: "info", label: { en: "Approved", ar: "معتمدة" } },
    Active: { kind: "info", label: { en: "Active", ar: "نشطة" } },
    PartiallyDispensed: { kind: "part", label: { en: "Partially dispensed", ar: "صُرفت جزئياً" } },
    Dispensed: { kind: "ok", label: { en: "Dispensed", ar: "صُرفت" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغاة" } },
  };
  return map[k] ?? map.Approved;
};

/**
 * Demo drug labels for the seeded prescription lines. The pharmacy dispensing projection is min-necessary and
 * carries only the masterdata drug_id (name resolution is a separate masterdata read, not part of the queue);
 * for the seeded rows we map those ids to their real ATC + name so the queue renders a meaningful medication.
 * Unmapped ids fall back to a token — no fabricated names.
 */
const DEMO_DRUG_LABELS: Record<string, { atc: string; en: string; ar: string }> = {
  "40d46bd1-0200-4404-b424-d9cdd05391b4": { atc: "A10BA02", en: "Metformin 500mg", ar: "ميتفورمين 500مجم" },
  "26d41d0b-2046-4e20-89f3-3a4a951570b7": { atc: "C08CA01", en: "Amlodipine 10mg", ar: "أملوديبين 10مجم" },
  "3aa10944-02db-44b2-89c6-95100b09d372": { atc: "N02BE01", en: "Paracetamol 500mg", ar: "باراسيتامول 500مجم" },
};
const drugCoded = (drugId: unknown) => {
  const d = DEMO_DRUG_LABELS[String(drugId)];
  return d
    ? { system: "ATC" as const, code: d.atc, label: { en: d.en, ar: d.ar } }
    : { system: "ATC" as const, code: String(drugId ?? "").slice(0, 8), label: loc("Medication") };
};
/** prescriptionId → its line ids (in order), cached from the queue so dispense can target concrete lines. */
const rxLineIds = new Map<string, string[]>();
/** Map an authorization status → a resolved bilingual StatusKind for the approvals worklist. */
const authStatus = (s: unknown) => {
  const k = String(s ?? "Submitted");
  const map: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: { en: string; ar: string } }> = {
    Submitted: { kind: "info", label: { en: "Submitted", ar: "مُقدَّم" } },
    UnderReview: { kind: "part", label: { en: "Under review", ar: "قيد المراجعة" } },
    Approved: { kind: "ok", label: { en: "Approved", ar: "معتمد" } },
    PartiallyApproved: { kind: "part", label: { en: "Partially approved", ar: "معتمد جزئياً" } },
    Rejected: { kind: "bad", label: { en: "Rejected", ar: "مرفوض" } },
    InfoRequested: { kind: "warn", label: { en: "Info requested", ar: "طُلبت معلومات" } },
    EmergencyApproved: { kind: "ok", label: { en: "Emergency approved", ar: "اعتماد طارئ" } },
    Overridden: { kind: "warn", label: { en: "Overridden", ar: "تجاوز" } },
    Expired: { kind: "neu", label: { en: "Expired", ar: "منتهٍ" } },
  };
  return map[k] ?? map.Submitted;
};
/** Decision kind → the approvals-service endpoint segment (decisions are per-type, not a single /decision). */
const decisionPath: Record<string, string> = {
  approve: "approve",
  reject: "reject",
  partial: "partially-approve",
  request_info: "request-info",
};

/**
 * Last reception search cards, keyed by beneficiaryId. The reception service returns ONE min-necessary card that
 * already carries identity + coverage + remaining limits; the fixture-era client split this into search+check, so
 * we cache the card from the search and let {@link HttpApiClient.checkEligibility} map it — no second round-trip
 * and no fabricated PHI (the card is the single source of truth reception is allowed to see).
 */
const receptionCards = new Map<string, any>();

/**
 * The production API client — talks to the phase services through the gateway (`/api/v1`), zod-validating
 * every response against the shared contract, and sending `Idempotency-Key` on consume/dispense/decide.
 *
 * It is fully wired but not exercised by the dev app (which uses `DevApiClient` fixtures) nor the tests; it is
 * the drop-in the app uses once the services are reachable behind Kong — exactly the AuthClient→OIDC pattern.
 */
export class HttpApiClient implements ApiClient {
  // Reception (Phase 2, US-010) — the eligibility service exposes ONE min-necessary reception card at
  // `/reception/search`; there is deliberately no full-demographic "get by id" for reception (Reception≠EMR).
  // We adapt the card into the search-hit + result-card contract, caching the card so the check step needs no
  // second call (and never fabricates DOB/gender the card intentionally omits).
  async searchEligibility(query: string) {
    const r = (await getRaw(`/reception/search?q=${encodeURIComponent(query)}`)) as any;
    const cards: any[] = r?.results ?? [];
    receptionCards.clear();
    for (const c of cards) receptionCards.set(String(c.identity?.beneficiaryId), c);
    return cards.map((c: any) =>
      parseOr(zEligibilityHit, {
        id: c.identity?.beneficiaryId,
        name: loc(c.identity?.displayName),
        cardNumber: c.identity?.memberNo ?? "",
      }),
    );
  }
  async checkEligibility(beneficiaryId: string) {
    const c = receptionCards.get(String(beneficiaryId));
    const identity = c?.identity ?? {};
    const categories: string[] = c?.coverage ?? [];
    const limits: any[] = c?.remainingLimits ?? [];
    // Pick a monetary remaining-limit (annual cap) for the coverage summary, if the card carries one.
    const cap = limits.find((l) => /amount|annual/i.test(String(l.limitType)));
    const active = String(identity.status ?? "").toLowerCase() === "active";
    return parseOr(zEligibilityResult, {
      verdict: statusToVerdict(identity.status),
      status: { kind: toneToKind(identity.statusSemantics?.tone), label: loc(identity.statusSemantics?.label ?? identity.status) },
      beneficiary: {
        id: identity.beneficiaryId ?? beneficiaryId,
        name: loc(identity.displayName),
        cardNumber: identity.memberNo ?? "",
      },
      coverage: categories.length
        ? {
            planName: { en: "Benefit coverage", ar: "التغطية التأمينية" },
            band: loc(categories.join(" · ")),
            annualCapRemaining: cap ? money(cap.remaining) : undefined,
          }
        : null,
      visitGate: active
        ? { allowed: true }
        : { allowed: false, reason: { en: "Coverage not active — refer to eligibility desk.", ar: "التغطية غير فعّالة — يُرجى مراجعة مكتب الأهلية." } },
    });
  }

  // Doctor / EMR (Phase 4, US-030) — the emr service is encounter-centric and treating-relationship gated: the
  // "my patients" worklist is the caller's own encounters (/encounters/mine), and a patient row's id IS its
  // encounter id, so getEncounter maps straight to /encounters/{id}/clinical. emr stores the beneficiary id but
  // not the name (that lives in patient-service), so we render a masked token — the doctor's zone still shows
  // full clinical detail (diagnoses/SOAP/vitals) that no other zone may see.
  async listPatients() {
    const r = (await getRaw(`/encounters/mine`)) as any[];
    return (r ?? []).map((e: any) =>
      parseOr(zPatientListItem, {
        id: e.encounterId,
        name: loc(`Beneficiary •••${String(e.beneficiaryId ?? "").slice(-4)}`),
        mrn: e.encounterNo ?? "",
        treating: true,
        lastVisit: e.startedAt ? String(e.startedAt).slice(0, 10) : null,
        status: encounterStatus(e.status),
      }),
    );
  }
  async getEncounter(encounterId: string) {
    const r = (await getRaw(`/encounters/${encodeURIComponent(encounterId)}/clinical`)) as any;
    const e = r?.encounter ?? {};
    const note = (r?.notes ?? [])[0] ?? {};
    const vitals: any[] = r?.vitals ?? [];
    const v = (type: string) => vitals.find((x) => String(x.vitalType) === type)?.valueNum ?? null;
    return parseOr(zEncounter, {
      id: e.encounterId ?? encounterId,
      patientId: e.beneficiaryId ?? "",
      patientName: loc(`Beneficiary •••${String(e.beneficiaryId ?? "").slice(-4)}`),
      openedAt: e.startedAt ?? new Date().toISOString(),
      signed: (r?.notes ?? []).some((n: any) => n.isSigned),
      soap: {
        subjective: note.subjective ?? "",
        objective: note.objective ?? "",
        assessment: note.assessment ?? "",
        plan: note.plan ?? "",
      },
      vitals: {
        heightCm: v("Height"),
        weightKg: v("Weight"),
        systolic: v("BP"),
        diastolic: null,
        heartRate: v("HR"),
        tempC: v("Temp"),
      },
      allergies: (r?.allergies ?? []).map((a: any) => ({
        id: a.allergyId,
        substance: loc(a.reaction ?? "Allergen"),
        severity: String(a.severity ?? "mild").toLowerCase(),
      })),
      diagnoses: (r?.diagnoses ?? []).map((d: any) => ({
        system: "ICD-10",
        code: d.icdCode,
        label: loc(d.icdCode),
      })),
    });
  }
  placeOrder(req: PlaceOrderRequest) {
    return postJson(`/orders`, req, zPlaceOrderResult);
  }
  prescribe(req: PrescribeRequest) {
    return postJson(`/prescriptions`, req, zPrescribeResult);
  }

  // Lab / Imaging (Phase 5, US-040) — the orders service exposes ONE capability-filtered provider queue at
  // /investigation-orders/queue (a lab_tech sees Lab orders, an imaging_tech Imaging — by role, not URL). We
  // flatten each order to one row using its first available line as the `test`, cache that line id so consume
  // can target it, and default priority to routine (the fulfillment queue does not carry a clinical priority).
  async labQueue(kind: "lab" | "imaging") {
    const r = (await getRaw(`/investigation-orders/queue?page=1&pageSize=50`)) as any[];
    return (r ?? [])
      .filter((o: any) => String(o.orderType ?? "").toLowerCase() === kind)
      .map((o: any) => {
        const lines: any[] = o.lines ?? [];
        const line = lines[0] ?? {};
        if (line.orderLineId) orderLineByOrderId.set(String(o.orderId), String(line.orderLineId));
        const remaining = lines.reduce((acc, l) => acc + Math.max(0, Math.round(Number(l.quantityRemaining ?? 1))), 0);
        return parseOr(zLabOrder, {
          id: o.orderId,
          kind,
          test: { system: codeSystem(line.codeSystem), code: line.code ?? "—", label: loc(line.description ?? line.code ?? "") },
          patient: { id: o.beneficiaryId, token: caseToken({ beneficiaryId: o.beneficiaryId }) },
          priority: "routine",
          status: orderStatus(o.status),
          placedAt: o.requestedAt ?? new Date().toISOString(),
          panelsTotal: Math.max(1, remaining),
          panelsDone: 0,
        });
      });
  }
  async consume(req: ConsumeRequest) {
    const orderLineId = orderLineByOrderId.get(String(req.orderId));
    const body = { lines: orderLineId ? [{ orderLineId, quantity: req.panels }] : [] };
    const r = (await postRaw(`/investigation-orders/${encodeURIComponent(req.orderId)}/consume`, body, req.idempotencyKey)) as any;
    const lines: any[] = r?.lines ?? [];
    const total = lines.reduce((acc, l) => acc + Math.round(Number(l.quantityOrdered ?? l.quantityRemaining ?? 1)), 0);
    const done = lines.reduce((acc, l) => acc + Math.round(Number(l.quantityConsumed ?? 0)), 0);
    return parseOr(zConsumeResult, {
      orderId: r?.orderId ?? req.orderId,
      fulfillmentId: (r?.fulfillments ?? [])[0]?.fulfillmentId ?? req.idempotencyKey,
      status: orderStatus(r?.orderStatus),
      panelsDone: done,
      panelsTotal: Math.max(1, total),
      replayed: !!r?.replayed,
    });
  }

  // Pharmacy (Phase 6, US-050) — the pharmacy service exposes a browse-all dispensable queue at
  // /prescriptions/queue (min-necessary: quantities + dose, never diagnosis). The contract's single-request
  // multi-line dispense maps to the service's per-line dispense endpoint (one atomic idempotent call per line);
  // batch/expiry are required by the service but not collected by this screen, so we supply a dev batch + a
  // one-year expiry per line. Line ids are cached from the queue so dispense can target them.
  async pharmacyQueue() {
    const r = (await getRaw(`/prescriptions/queue`)) as any[];
    return (r ?? []).map((p: any) => {
      const lines: any[] = p.lines ?? [];
      rxLineIds.set(String(p.prescriptionId), lines.map((l) => String(l.prescriptionLineId)));
      return parseOr(zPrescription, {
        id: p.prescriptionId,
        patient: { id: p.beneficiaryId, token: caseToken({ beneficiaryId: p.beneficiaryId }) },
        prescriber: { label: loc("Prescriber") },
        submittedAt: p.submittedAt ?? new Date().toISOString(),
        status: rxStatus(p.status),
        lines: lines.map((l) => ({
          id: l.prescriptionLineId,
          drug: drugCoded(l.drugId),
          quantity: Math.max(1, Math.round(Number(l.quantityPrescribed ?? 1))),
          dispensed: Math.round(Number(l.quantityDispensed ?? 0)),
          dose: [l.dose, l.route, l.frequency].filter(Boolean).join(" · "),
          status: rxStatus(l.status),
          outOfStock: false,
        })),
      });
    });
  }
  async dispense(req: DispenseRequest) {
    const expiry = new Date(Date.now() + 365 * 24 * 3600 * 1000).toISOString().slice(0, 10);
    let last: any = null;
    for (const line of req.lines) {
      last = await postRaw(
        `/prescriptions/${encodeURIComponent(req.prescriptionId)}/lines/${encodeURIComponent(line.lineId)}/dispense`,
        { quantity: line.quantity, batchNo: `DEV-${String(req.prescriptionId).slice(0, 8)}`, expiryDate: expiry },
        `${req.idempotencyKey}:${line.lineId}`,
      );
    }
    const rx = last?.prescription ?? {};
    const outstanding = (rx.lines ?? []).filter((l: any) => Number(l.quantityRemaining ?? 0) > 0).length;
    return parseOr(zDispenseResult, {
      prescriptionId: req.prescriptionId,
      dispenseEventId: last?.dispense?.dispenseEventId ?? req.idempotencyKey,
      status: rxStatus(last?.rxStatus ?? rx.status),
      replayed: !!last?.replayed,
      linesOutstanding: outstanding,
    });
  }

  // Approvals (Phase 7, US-060) — the worklist is GET /authorizations/ (min-necessary: codes + SLA, NO clinical
  // payload — that is /review only, audited as a PHI read). Decisions are per-type endpoints, not one /decision;
  // a decision needs the request UnderReview, so decide assigns first (idempotent-ish) then routes by kind.
  async approvalWorklist() {
    const r = (await getRaw(`/authorizations/`)) as any[];
    const now = Date.now();
    return (r ?? []).map((a: any) => {
      const dueMs = a.slaDueAt ? Date.parse(a.slaDueAt) : now;
      const submittedAt = new Date(now - Number(a.tatElapsedSeconds ?? 0) * 1000).toISOString();
      const code = (a.serviceCodes ?? [])[0] ?? "—";
      return parseOr(zApprovalItem, {
        id: a.authorizationId,
        patient: { id: a.beneficiaryId, token: caseToken({ beneficiaryId: a.beneficiaryId }) },
        service: { system: "CPT", code, label: loc(code) },
        requestedBy: loc("Provider"),
        priority: String(a.priority ?? "routine").toLowerCase(),
        sla: {
          dueAt: a.slaDueAt ?? submittedAt,
          breached: !!a.slaBreached,
          minutesRemaining: Math.round((dueMs - now) / 60000),
        },
        status: authStatus(a.status),
        submittedAt,
        estimatedCost: "—",
      });
    });
  }
  async approvalReview(approvalId: string) {
    const a = (await getRaw(`/authorizations/${encodeURIComponent(approvalId)}/review`)) as any;
    const codes: string[] = a?.serviceCodes ?? [];
    return parseOr(zApprovalReview, {
      id: a?.authorizationId ?? approvalId,
      patient: { id: a?.beneficiaryId ?? "", token: caseToken({ beneficiaryId: a?.beneficiaryId }) },
      service: { system: "CPT", code: codes[0] ?? "—", label: loc(codes[0] ?? "") },
      clinicalJustification: a?.emrSummary ?? "clinical context unavailable",
      supportingCodes: codes.slice(1).map((c) => ({ system: "CPT" as const, code: c, label: loc(c) })),
      documents: (a?.documents ?? []).map((d: any) => ({ id: d.id ?? d.documentId ?? "", name: d.name ?? d.title ?? "document" })),
      requestedAmount: "—",
    });
  }
  async decide(req: DecisionRequest) {
    const seg = decisionPath[req.decision] ?? "approve";
    const base = `/authorizations/${encodeURIComponent(req.approvalId)}`;
    // Move Submitted → UnderReview so the decision is legal; ignore if already assigned/underway.
    try {
      await postRaw(`${base}/assign`, {}, `${req.idempotencyKey}:assign`);
    } catch {
      /* already assigned or not assignable — proceed to the decision, which will report any real conflict */
    }
    const body =
      req.decision === "partial"
        ? { approvedScope: req.approvedAmount ? [req.approvedAmount] : [], rationale: req.rationale }
        : { rationale: req.rationale };
    const r = (await postRaw(`${base}/${seg}`, body, req.idempotencyKey)) as any;
    return parseOr(zDecisionResult, {
      approvalId: r?.authorizationId ?? req.approvalId,
      decisionId: r?.decisionId ?? r?.id ?? req.idempotencyKey,
      status: authStatus(r?.status),
      replayed: !!r?.replayed,
    });
  }

  // Director / Reporting (Phase 8.3) — the reporting service emits one executive dashboard at
  // /dashboards/executive (zone-tagged widgets, each with chart series + a mandatory accessible dataTable +
  // bilingual labels, PHI-free). The zod contract splits widgets into kpis + charts, so we map gauge/summary
  // widgets to KPI cards and the rest to charts (every chart keeps its required dataTable — US-073).
  async executiveDashboard(scope: "executive" | "finance" | "director") {
    const d = (await getRaw(`/dashboards/executive`)) as any;
    const widgets: any[] = d?.widgets ?? [];
    const bi = (x: any) => ({ en: String(x?.en ?? ""), ar: String(x?.ar ?? "") });
    const points = (w: any) => (w.series?.[0]?.points ?? []) as any[];
    const chartTypeByKind: Record<number, "bar" | "line" | "donut"> = { 0: "line", 1: "bar", 2: "donut", 3: "bar", 4: "donut", 5: "bar" };
    const isKpi = (w: any) => w.kind === 2 || w.kind === 5; // Gauge | Summary → KPI card

    const kpis = widgets.filter(isKpi).map((w) =>
      parseOr(zKpiWidget, {
        kind: "kpi",
        id: w.key,
        title: bi(w.title),
        value: String(points(w).reduce((acc: number, p: any) => acc + Number(p.value ?? 0), 0)),
      }),
    );
    const charts = widgets.filter((w) => !isKpi(w)).map((w) =>
      parseOr(zChartWidget, {
        kind: "chart",
        id: w.key,
        title: bi(w.title),
        chartType: chartTypeByKind[w.kind as number] ?? "bar",
        series: points(w).map((p: any) => ({ label: loc(p.label), value: Number(p.value ?? 0), display: String(p.value ?? 0) })),
        dataTable: {
          columns: (w.dataTable?.columns ?? []).map(bi),
          rows: (w.dataTable?.rows ?? []).map((row: any[]) => row.map((c) => String(c))),
        },
      }),
    );
    return parseOr(zExecutiveDashboard, {
      version: d?.contractVersion ?? "1.0",
      generatedAt: d?.generatedAt ?? new Date().toISOString(),
      scope,
      kpis,
      charts,
    });
  }

  // Case management (Phase 10.1) — assignment-scoped; the server re-authorizes every call (case-assignment ABAC).
  // The service returns { items } with PascalCase enums + a plain summary; adapt to the array + lowercase-enum
  // + bilingual contract shape.
  async myCases() {
    const r = (await getRaw(`/cases`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((c: any) =>
      parseOr(zCaseListItem, {
        id: c.caseId ?? c.id,
        caseNo: c.caseNo,
        beneficiary: { id: c.beneficiaryId ?? c.beneficiary?.id ?? c.caseId, token: caseToken(c) },
        category: String(c.category ?? "complex").toLowerCase(),
        priority: String(c.priority ?? "normal").toLowerCase(),
        status: caseStatus(c.status),
        openedAt: c.openedAt ?? new Date().toISOString(),
        summary: c.summary ? loc(c.summary) : undefined,
      }),
    );
  }
  beneficiary360(caseId: string) {
    return getJson(`/cases/${encodeURIComponent(caseId)}/beneficiary-360`, zBeneficiary360);
  }
  async caseTasks(caseId: string) {
    const r = (await getRaw(`/cases/${encodeURIComponent(caseId)}/tasks`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((t: any) =>
      parseOr(zCoordinationTask, {
        id: t.taskId ?? t.id,
        caseId: t.caseId ?? caseId,
        title: loc(t.title ?? t.description ?? ""),
        state: String(t.state ?? "todo").toLowerCase().replace(/inprogress/, "in_progress"),
        dueAt: t.dueAt ?? undefined,
        status: "ok",
      }),
    );
  }
  async escalations() {
    const r = (await getRaw(`/cases/escalations`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((e: any) =>
      parseOr(zEscalation, {
        id: e.escalationId ?? e.id,
        caseId: e.caseId ?? "",
        caseNo: e.caseNo ?? "",
        raisedToRole: loc(e.raisedToRole ?? e.targetRole ?? ""),
        reason: String(e.reason ?? ""),
        status: "ok",
        raisedAt: e.raisedAt ?? e.createdAt ?? new Date().toISOString(),
      }),
    );
  }

  // Finance (Phase 10.2) — billing codes + amounts only; the finance service denies any clinical read.
  // The service emits plain strings + numeric amounts; these adapters map to the bilingual + pre-formatted
  // contract shape (and compute share%), then validate the mapping.
  async utilization() {
    const r = (await getRaw(`/finance/utilization`)) as any;
    return parseOr(zUtilizationView, {
      from: r?.from ?? "",
      to: r?.to ?? "",
      rows: (r?.rows ?? []).map((x: any) => ({
        serviceCode: x.serviceCode,
        serviceLine: loc(x.serviceLine),
        coverageCategory: loc(x.coverageCategory),
        providerRef: x.providerRef ?? undefined,
        authorizedQty: x.authorizedQty ?? 0,
        deliveredQty: x.deliveredQty ?? 0,
        spend: money(x.spend),
      })),
      totalAuthorized: r?.totalAuthorized ?? 0,
      totalDelivered: r?.totalDelivered ?? 0,
      totalSpend: money(r?.totalSpend),
    });
  }
  async settlements() {
    const r = (await getRaw(`/finance/settlements`)) as any[];
    return (r ?? []).map((s: any) =>
      parseOr(zSettlement, {
        id: s.id,
        settlementNo: s.settlementNo,
        providerRef: s.providerRef ?? s.providerId ?? "",
        providerName: loc(s.providerName ?? s.providerRef ?? ""),
        periodStart: s.periodStart ?? "",
        periodEnd: s.periodEnd ?? "",
        currency: s.currency ?? "EGP",
        total: money(s.total),
        status: "ok",
        state: String(s.state ?? s.status ?? "draft").toLowerCase(),
        lines: (s.lines ?? []).map((l: any) => ({
          serviceCode: l.serviceCode,
          serviceLine: loc(l.serviceLine),
          deliveredQty: l.deliveredQty ?? 0,
          agreedUnitPrice: money(l.agreedUnitPrice),
          lineTotal: money(l.lineTotal),
        })),
      }),
    );
  }
  async financialSummary(dimension: "serviceline" | "category" | "provider") {
    const r = (await getRaw(`/finance/summaries?dimension=${dimension}`)) as any;
    const buckets: any[] = r?.buckets ?? [];
    const total = buckets.reduce((acc, b) => acc + Number(b.spend ?? 0), 0) || 1;
    return parseOr(zFinancialSummary, {
      dimension: r?.dimension ?? dimension,
      buckets: buckets.map((b: any) => ({
        key: loc(b.key),
        deliveredQty: b.deliveredQty ?? 0,
        spend: money(b.spend),
        sharePercent: Math.round((Number(b.spend ?? 0) / total) * 100),
      })),
      totalSpend: money(r?.totalSpend ?? total),
    });
  }
  async exportReport(req: ExportRequest) {
    const r = (await postRaw(`/finance/exports`, req)) as any;
    return parseOr(zExportResult, {
      report: r?.report ?? req.report,
      format: r?.format ?? req.format,
      rowCount: r?.rowCount ?? r?.rows ?? 0,
      filename: r?.filename ?? `${req.report}-${req.from}_${req.to}.${req.format}`,
      status: "ok",
    });
  }
}
