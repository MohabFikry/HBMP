import { z } from "zod";
import { zId, zLocalized } from "./common";

/**
 * Formulary reference contracts (Phase 6.3, US-052). Back the pharmacist's substitutions lookup — a drug and
 * its policy-approved alternatives (the same ATC-5 therapeutic substance). Reference data only (master data),
 * never PHI. Names are bilingual (AR from master data where present, else the EN name echoed).
 */
export const zDrugRef = z.object({
  drugId: zId,
  name: zLocalized,
  atcCode: z.string().optional(),
  form: z.string().optional(),
  strength: z.string().optional(),
});
export type DrugRef = z.infer<typeof zDrugRef>;
