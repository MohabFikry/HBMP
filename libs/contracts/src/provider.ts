import { z } from "zod";
import { zId, zStatus } from "./common";

/**
 * Provider-network contracts (Phase 2b, US-018..021). The Network Team administers the tenant's provider
 * directory — providers, their locations and contracts. Reference/administrative data (no beneficiary PHI).
 * Statuses render as non-color StatusChip kinds. Prices are omitted here (provider:finance-only).
 */
export const zProviderSummary = z.object({
  id: zId,
  code: z.string(),
  legalName: z.string(),
  providerType: z.string(),
  status: zStatus,
  onboardingState: z.string(),
});
export type ProviderSummary = z.infer<typeof zProviderSummary>;

export const zProviderLocation = z.object({
  id: zId,
  name: z.string(),
  governorate: z.string().optional(),
  address: z.string().optional(),
  isPrimary: z.boolean(),
});
export type ProviderLocation = z.infer<typeof zProviderLocation>;

export const zProviderContract = z.object({
  id: zId,
  contractNo: z.string(),
  status: zStatus,
  effectiveFrom: z.string(),
  effectiveTo: z.string().optional(),
  serviceLines: z.number().int(),
});
export type ProviderContract = z.infer<typeof zProviderContract>;

/** New-provider onboarding request (Network Team). */
export const zCreateProviderInput = z.object({
  code: z.string().min(1),
  legalName: z.string().min(1),
  providerType: z.enum(["Hospital", "Clinic", "Lab", "Pharmacy", "Imaging"]),
});
export type CreateProviderInput = z.infer<typeof zCreateProviderInput>;
