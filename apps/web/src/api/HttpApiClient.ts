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

  listPatients() {
    return getJson(`/emr/patients`, z.array(zPatientListItem));
  }
  getEncounter(patientId: string) {
    return getJson(`/emr/patients/${encodeURIComponent(patientId)}/encounter`, zEncounter);
  }
  placeOrder(req: PlaceOrderRequest) {
    return postJson(`/orders`, req, zPlaceOrderResult);
  }
  prescribe(req: PrescribeRequest) {
    return postJson(`/prescriptions`, req, zPrescribeResult);
  }

  labQueue(kind: "lab" | "imaging") {
    return getJson(`/${kind}/queue`, z.array(zLabOrder));
  }
  consume(req: ConsumeRequest) {
    return postJson(`/orders/${encodeURIComponent(req.orderId)}/consume`, req, zConsumeResult, req.idempotencyKey);
  }

  pharmacyQueue() {
    return getJson(`/pharmacy/queue`, z.array(zPrescription));
  }
  dispense(req: DispenseRequest) {
    return postJson(
      `/pharmacy/${encodeURIComponent(req.prescriptionId)}/dispense`,
      req,
      zDispenseResult,
      req.idempotencyKey,
    );
  }

  approvalWorklist() {
    return getJson(`/authorizations/worklist`, z.array(zApprovalItem));
  }
  approvalReview(approvalId: string) {
    return getJson(`/authorizations/${encodeURIComponent(approvalId)}/review`, zApprovalReview);
  }
  decide(req: DecisionRequest) {
    return postJson(
      `/authorizations/${encodeURIComponent(req.approvalId)}/decision`,
      req,
      zDecisionResult,
      req.idempotencyKey,
    );
  }

  executiveDashboard(scope: "executive" | "finance" | "director") {
    return getJson(`/reports/dashboards/${scope}`, zExecutiveDashboard);
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
