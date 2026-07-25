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
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type PlaceOrderRequest,
  type PrescribeRequest,
} from "@mersal/contracts";
import type { ApiClient } from "./client";
import { getJson, postJson } from "./http";

/**
 * The production API client — talks to the phase services through the gateway (`/api/v1`), zod-validating
 * every response against the shared contract, and sending `Idempotency-Key` on consume/dispense/decide.
 *
 * It is fully wired but not exercised by the dev app (which uses `DevApiClient` fixtures) nor the tests; it is
 * the drop-in the app uses once the services are reachable behind Kong — exactly the AuthClient→OIDC pattern.
 */
export class HttpApiClient implements ApiClient {
  searchEligibility(query: string) {
    return getJson(`/eligibility/search?q=${encodeURIComponent(query)}`, z.array(zEligibilityHit));
  }
  checkEligibility(beneficiaryId: string) {
    return getJson(`/eligibility/${encodeURIComponent(beneficiaryId)}`, zEligibilityResult);
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
}
