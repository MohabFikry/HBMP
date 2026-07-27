import { z } from "zod";
import { zId, zInstant, zStatus, zPatientRef } from "./common";

/**
 * Reception day-board contracts (Phase 3, US-020). Reception is front-desk, not clinical — a row carries a
 * masked beneficiary token + appointment type/time/status only (never diagnosis or clinical notes). Every
 * status is pre-resolved to a non-color StatusChip kind (accessibility: hue + text). `checkInEligible` tells
 * the desk which rows can be checked in without the screen re-deriving the state machine.
 */
export const zAppointmentRow = z.object({
  id: zId,
  beneficiary: zPatientRef,
  /** Appointment type as the emr enum name (e.g. "Consultation", "FollowUp") — display verbatim. */
  appointmentType: z.string(),
  status: zStatus,
  scheduledStart: zInstant,
  /** True when the appointment is Booked (the only state reception can check in). */
  checkInEligible: z.boolean(),
  /**
   * 18.D1 (audit R2 E3) — the SERVER's answer to "has this patient arrived?", so the desk renders confirmed
   * state instead of a local "we sent the request" flag. Distinct from `!checkInEligible`, which is also
   * false for a cancelled or no-show appointment — three different situations the receptionist must be able
   * to tell apart.
   */
  checkedIn: z.boolean(),
  /** The emr row's `xmin` optimistic-concurrency token (opt-in): echoed as `If-Match` on check-in so a
   * stale board loses to a concurrent transition with 412 instead of silently double-acting. Optional —
   * absent for a fixture/older service, in which case check-in proceeds without the guard. */
  rowVersion: z.number().int().optional(),
});
export type AppointmentRow = z.infer<typeof zAppointmentRow>;

/** Result of checking a beneficiary in at the desk (Booked → CheckedIn, enqueues a walk-in ticket). */
export const zCheckInResult = z.object({
  id: zId,
  status: zStatus,
});
export type CheckInResult = z.infer<typeof zCheckInResult>;
