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
  /**
   * The SERVER's answer to "may this be marked a no-show yet?" — a Booked appointment whose scheduled end has
   * passed by the grace period. Never re-derived from the browser clock: too early is a 409 the receptionist
   * cannot explain, too late leaves a patient who never came sitting Booked all day, and a clinic PC with a
   * wrong clock would be wrong in whichever direction it drifted.
   */
  noShowEligible: z.boolean(),
  /** True when a visit may be started from this row: CheckedIn, and assigned to the caller (or unassigned).
   *  The server owns it — the doctor's board must not re-derive a treating-relationship rule. */
  startVisitEligible: z.boolean(),
  /** The emr row's `xmin` optimistic-concurrency token (opt-in): echoed as `If-Match` on check-in so a
   * stale board loses to a concurrent transition with 412 instead of silently double-acting. Optional —
   * absent for a fixture/older service, in which case check-in proceeds without the guard. */
  rowVersion: z.number().int().optional(),
});
export type AppointmentRow = z.infer<typeof zAppointmentRow>;

/** Result of a desk transition (check-in, no-show) — the row's new server-confirmed status. */
export const zCheckInResult = z.object({
  id: zId,
  status: zStatus,
});
export type CheckInResult = z.infer<typeof zCheckInResult>;

/**
 * A bookable slot (Phase 3.1). The SERVER decides `open` — it holds the no-double-book invariant and knows
 * about held slots the desk cannot see — so the screen never re-derives availability from times.
 */
export const zBookableSlot = z.object({
  id: zId,
  start: zInstant,
  end: zInstant,
  open: z.boolean(),
  /** Present when the slot belongs to a named practitioner rather than a general clinic session. */
  doctorId: zId.optional(),
});
export type BookableSlot = z.infer<typeof zBookableSlot>;

/**
 * A booking request from the desk. There is deliberately no `branchId`: a BranchScoped caller's branch is
 * resolved server-side from their active branch, and a request that names another one is refused rather than
 * silently rewritten. The call centre, which books across branches, states its branch explicitly instead.
 */
export const zBookingRequest = z.object({
  beneficiaryId: zId,
  providerId: zId,
  locationId: zId,
  slotId: zId,
  appointmentType: z.string().min(1),
  /** Cross-branch callers only (call centre). Omitted by a branch-scoped desk. */
  branchId: zId.optional(),
});
export type BookingRequest = z.infer<typeof zBookingRequest>;

/** What the desk shows after a successful booking — enough to confirm to the patient, nothing clinical. */
export const zBookingResult = z.object({
  id: zId,
  status: zStatus,
  scheduledStart: zInstant,
});
export type BookingResult = z.infer<typeof zBookingResult>;

/**
 * A clinic the desk may book into. Derived server-side from the slots that exist in the caller's branch, not
 * from the provider directory — the front desk is correctly refused `provider:read`, and a clinic with no
 * availability should never be offerable in the first place.
 */
export const zBookableClinic = z.object({
  providerId: zId,
  locationId: zId,
  /** The branch this clinic sits in. Carried so a CROSS-branch caller (the call centre) can state the branch it
   *  is booking into without a second picker — the clinic already determines it. Null for an external
   *  provider location, which belongs to no Mersal branch. */
  branchId: zId.nullish(),
  /** Display label, resolved from the label lookup; falls back to the ids when unavailable. */
  label: z.string().min(1),
  openSlots: z.number().int().nonnegative(),
});
export type BookableClinic = z.infer<typeof zBookableClinic>;

/**
 * One step of an appointment's operational history: the status it moved into, when, and who did it. `by` is
 * absent for transitions recorded before actor attribution existed — showing no actor is honest, where falling
 * back to whoever booked it would claim they performed a step they did not.
 *
 * This is the OPERATIONAL timeline, not the compliance audit trail: that lives in audit-service, is
 * hash-chained, and requires audit:read (Security/Compliance/DPO).
 */
export const zTimelineStep = z.object({
  status: z.string().min(1),
  at: zInstant,
  by: z.string().nullish(),
});
export type TimelineStep = z.infer<typeof zTimelineStep>;
