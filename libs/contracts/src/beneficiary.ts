import { z } from "zod";
import { zId, zStatus } from "./common";

/**
 * Beneficiary-management contracts (Phase 1, US-001..005). The Beneficiary-Management role administers the
 * beneficiary registry — register, search/manage, status & reactivation — a min-necessary identity projection
 * (name + member no + identifiers + status), never clinical data. Statuses render as non-color StatusChip kinds.
 */
export const zBeneficiaryIdentifier = z.object({
  type: z.string(),
  value: z.string(),
  isPrimary: z.boolean(),
});

export const zBeneficiaryRow = z.object({
  id: zId,
  memberNo: z.string().optional(),
  givenName: z.string(),
  familyName: z.string(),
  status: zStatus,
  /** Raw status enum name (Pending/Active/Suspended/…) for the status-change screen. */
  statusRaw: z.string(),
  identifiers: z.array(zBeneficiaryIdentifier),
});
export type BeneficiaryRow = z.infer<typeof zBeneficiaryRow>;

/** New-beneficiary registration (one primary identifier + one primary phone is the min viable record). */
export const zRegisterBeneficiaryInput = z.object({
  givenName: z.string().min(1),
  familyName: z.string().min(1),
  birthDate: z.string().optional(),
  sex: z.enum(["Male", "Female", "Other", "Unknown"]).optional(),
  identifierType: z.enum(["NationalID", "Passport", "RefugeeID", "UNHCRNo"]),
  identifierValue: z.string().min(1),
  phone: z.string().optional(),
});
export type RegisterBeneficiaryInput = z.infer<typeof zRegisterBeneficiaryInput>;

export const zRegisterResult = z.object({
  id: zId,
  memberNo: z.string().optional(),
  status: zStatus,
});
export type RegisterResult = z.infer<typeof zRegisterResult>;

/** Outcome of a status change / reactivation. */
export const zStatusChangeResult = z.object({
  id: zId,
  status: zStatus,
});
export type StatusChangeResult = z.infer<typeof zStatusChangeResult>;
