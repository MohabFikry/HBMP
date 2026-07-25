import { describe, expect, it } from "vitest";
import { zChartWidget, zDecisionRequest } from "../src/index";

const UUID = "11111111-1111-4111-8111-111111111111";

describe("approvals decision (US-060)", () => {
  it("rejects a reject-decision with no rationale", () => {
    const r = zDecisionRequest.safeParse({
      approvalId: "a1",
      idempotencyKey: UUID,
      decision: "reject",
      rationale: "   ",
    });
    expect(r.success).toBe(false);
    if (!r.success) expect(r.error.issues.some((i) => i.message === "rationale.required")).toBe(true);
  });

  it("accepts an approve-decision with no rationale", () => {
    const r = zDecisionRequest.safeParse({ approvalId: "a1", idempotencyKey: UUID, decision: "approve" });
    expect(r.success).toBe(true);
  });

  it("requires an approved amount on a partial", () => {
    const r = zDecisionRequest.safeParse({
      approvalId: "a1",
      idempotencyKey: UUID,
      decision: "partial",
      rationale: "cover consult only",
    });
    expect(r.success).toBe(false);
  });

  it("requires an extra justification when break-glass is present", () => {
    const r = zDecisionRequest.safeParse({
      approvalId: "a1",
      idempotencyKey: UUID,
      decision: "approve",
      breakGlass: { kind: "emergency", justification: "" },
    });
    expect(r.success).toBe(false);
  });
});

describe("dashboard chart (US-073)", () => {
  it("cannot construct a chart without a data-table", () => {
    const r = zChartWidget.safeParse({
      kind: "chart",
      id: "c1",
      title: { en: "TAT", ar: "زمن" },
      chartType: "bar",
      series: [{ label: { en: "A", ar: "أ" }, value: 1, display: "1" }],
      // dataTable intentionally omitted
    });
    expect(r.success).toBe(false);
  });
});
