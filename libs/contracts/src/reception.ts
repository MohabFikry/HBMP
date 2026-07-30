import { z } from "zod";
import { zDate, zId, zInstant, zStatus, zPatientRef } from "./common";

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
  /** The branch this appointment belongs to, and its name once resolved. Null for an external provider
   *  location, which belongs to no Mersal branch. A cross-branch board is unreadable without it. */
  branchId: zId.nullish(),
  branchName: z.string().nullish(),
  /** The emr row's `xmin` optimistic-concurrency token (opt-in): echoed as `If-Match` on check-in so a
   * stale board loses to a concurrent transition with 412 instead of silently double-acting. Optional —
   * absent for a fixture/older service, in which case check-in proceeds without the guard. */
  rowVersion: z.number().int().optional(),
  /**
   * The GENERAL/administrative booking note, when one was written — access needs, an interpreter, an
   * arrangement. Shared between reception, the call centre and the treating doctor.
   *
   * <b>Never clinical.</b> The call centre writes this field and holds no clinical surface anywhere else on
   * the platform, so it is the one place clinical detail could accumulate across that line; emr caps it and
   * the schema caps it again. Absent (rather than empty) when no note was written, so the row renders no
   * note affordance at all instead of one that opens onto nothing.
   */
  note: z.string().nullish(),
  /**
   * The patient's display name, where the server has one — it is captured at CHECK-IN, so an arrived patient
   * has a name and a merely-booked appointment does not.
   *
   * Absent is "not known", never "withheld": reception seeing the name is a signed-off decision, since the
   * desk greets the patient and arranges their journey and a masked token does neither. `beneficiary.token`
   * remains the identity on boards that do not need the name.
   */
  beneficiaryName: z.string().nullish(),
  /**
   * The practitioner this appointment belongs to, when it names one. Null for a general clinic session that
   * belongs to whoever is on shift.
   *
   * An ID, not a name: who a practitioner is belongs to provider-service, and the screens that need the name
   * read it there under `practitioner:read` and join. emr returning the name would be one service composing
   * another's data on the caller's behalf.
   */
  doctorId: zId.nullish(),
  /**
   * The assigned doctor stopped serving this appointment's branch, so it needs a human decision — reassign,
   * rebook, or cancel.
   *
   * Nothing was done to the appointment automatically: cancelling would destroy booked care over an
   * administrative change, and reassigning would silently alter who the patient was told they would see.
   * The flag exists so the desk can act; it is the whole reconciliation.
   */
  needsReassignment: z.boolean().optional(),
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
  /** The doctor the appointment is for, when the desk chose one rather than a general clinic session. */
  doctorId: zId.optional(),
  /**
   * A short general/administrative note captured at booking. Capped at 500 characters by emr, which REFUSES
   * an over-long note rather than truncating it — so the form must enforce the same limit rather than letting
   * an operator write past it and lose the tail.
   */
  note: z.string().max(500).optional(),
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
 * A doctor who has open time, as emr reports it — an id and two numbers, no name and no specialty.
 *
 * That omission is the contract, not a gap to fill later. Who a practitioner IS belongs to provider-service,
 * and the booking screen reads it there directly under `practitioner:read`. Having emr return the name would
 * mean one service assembling a richer answer about another's data than the caller could obtain themselves,
 * which is the aggregation shape this platform forbids.
 *
 * So the screen holds two authorized reads and joins them: this says WHO CAN BE BOOKED, provider-service says
 * WHO THEY ARE. A doctor missing from either list is not offered — which is also how a clinician with a full
 * calendar disappears from the picker without anyone having to remember to hide them.
 */
export const zDoctorAvailability = z.object({
  doctorId: zId,
  branchId: zId.nullish(),
  openSlots: z.number().int().nonnegative(),
  /** Earliest open slot — lets the picker answer "who can see this patient soonest". */
  nextSlotStart: z.string(),
});
export type DoctorAvailability = z.infer<typeof zDoctorAvailability>;

/**
 * Open-slot count for one Cairo civil day — a cell in the booking calendar.
 *
 * `day` is a plain `YYYY-MM-DD`, not an instant, precisely so it cannot be re-zoned on the way to the screen
 * and land one cell to the left. The server has already decided which Cairo day each slot belongs to; the
 * calendar's job is to paint that answer, not to recompute it.
 */
export const zAppointmentDay = z.object({
  day: zDate,
  openSlots: z.number().int().nonnegative(),
});
export type AppointmentDay = z.infer<typeof zAppointmentDay>;

/**
 * The reception dashboard's three cards for one Cairo day, counted SERVER-side.
 *
 * Not tallied from the board: that read is capped at 200 rows, so on a busy day the cards would have
 * disagreed with reality — and disagreed downwards, which is the direction nobody notices.
 */
export const zAppointmentCounts = z.object({
  total: z.number().int().nonnegative(),
  checkedIn: z.number().int().nonnegative(),
  noShow: z.number().int().nonnegative(),
});
export type AppointmentCounts = z.infer<typeof zAppointmentCounts>;

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
  /** The actor's display name, when it could be resolved. Kept SEPARATE from `by` so the UI can render a name as
   *  a name and an unresolved id as an identifier — collapsing them would mean showing a GUID styled as a
   *  person, or worse, treating a name as an id and truncating it to eight characters. */
  byName: z.string().nullish(),
});
export type TimelineStep = z.infer<typeof zTimelineStep>;
