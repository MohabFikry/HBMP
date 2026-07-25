import { describe, expect, it } from "vitest";
import { z } from "zod";
import {
  zChartWidget,
  zDecisionRequest,
  zBeneficiary360,
  zUtilizationView,
  zSettlement,
  zFinancialSummary,
} from "../src/index";

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

/** Recursively collect every property name a zod object graph exposes (US-095 / finance ≠ diagnosis guard). */
function fieldNames(schema: z.ZodTypeAny, seen = new Set<z.ZodTypeAny>()): string[] {
  if (seen.has(schema)) return [];
  seen.add(schema);
  const def = (schema as z.ZodTypeAny)._def as { typeName?: string; [k: string]: unknown };
  const tn = def.typeName;
  if (tn === "ZodObject") {
    const shape = (schema as z.ZodObject<z.ZodRawShape>).shape;
    return Object.entries(shape).flatMap(([k, v]) => [k, ...fieldNames(v as z.ZodTypeAny, seen)]);
  }
  if (tn === "ZodArray") return fieldNames((def.type as z.ZodTypeAny), seen);
  if (tn === "ZodOptional" || tn === "ZodNullable") return fieldNames((def.innerType as z.ZodTypeAny), seen);
  return [];
}

const CLINICAL = ["diagnosis", "icd", "note", "prescription", "result", "symptom", "allergy", "clinical"];

describe("finance ≠ diagnosis (US-095)", () => {
  for (const [name, schema] of [
    ["utilization", zUtilizationView],
    ["settlement", zSettlement],
    ["summary", zFinancialSummary],
  ] as const) {
    it(`the ${name} contract exposes no clinical field`, () => {
      const names = fieldNames(schema).map((n) => n.toLowerCase());
      expect(names.some((n) => CLINICAL.some((c) => n.includes(c)))).toBe(false);
    });
  }
});

describe("beneficiary-360 is a coordination summary", () => {
  it("carries coord-visible diagnoses but only MASKED note/rx/result sections", () => {
    const clinical = zBeneficiary360.shape.clinical;
    const names = Object.keys(clinical.shape);
    // A diagnosis summary is allowed (coordination); notes/prescriptions/results exist ONLY as masked sections.
    expect(names).toContain("activeDiagnoses");
    expect(clinical.shape.notes.shape.summaryOnly).toBeDefined();
    // The masked section has a count but no field that can carry a record body.
    const noteFields = Object.keys(clinical.shape.notes.shape);
    expect(noteFields.sort()).toEqual(["count", "summaryOnly"]);
  });
});
