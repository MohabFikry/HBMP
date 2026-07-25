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
});
export type AppointmentRow = z.infer<typeof zAppointmentRow>;

/** Result of checking a beneficiary in at the desk (Booked → CheckedIn, enqueues a walk-in ticket). */
export const zCheckInResult = z.object({
  id: zId,
  status: zStatus,
});
export type CheckInResult = z.infer<typeof zCheckInResult>;
