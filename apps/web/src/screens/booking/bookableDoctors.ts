import type { DoctorAvailability, Practitioner, Specialty } from "@mersal/contracts";

/**
 * Joining "who can be booked" (emr) with "who they are" (provider-service).
 *
 * ============================================================================================================
 * WHY THIS IS A JOIN AND NOT ONE ENDPOINT
 * ============================================================================================================
 * The booking screen needs three things about a doctor — their name, their specialty, and whether they have
 * any time free. The first two are provider-service's; the third is emr's. No single service can answer all
 * three without reading another's data on the caller's behalf, and this platform forbids that shape outright
 * (see `NoServiceAccountArchitectureTests`: a privileged aggregator that fetches everything and then filters
 * is the classic aggregation vulnerability).
 *
 * So the client — which legitimately holds `practitioner:read` AND `appointment:read` — makes both reads and
 * joins them here. This is not a security filter and must never be mistaken for one: both inputs are already
 * projected to what the caller may see. It is the UX rule that a picker must not offer a choice that leads
 * nowhere.
 *
 * ============================================================================================================
 * WHY AN INNER JOIN IN BOTH DIRECTIONS
 * ============================================================================================================
 * A doctor absent from `availability` has no open slot: offering them produces a patient told to pick a time
 * and then shown none. A doctor absent from `practitioners` is not assigned to this branch, has been
 * suspended, or is a nurse — provider-service already applied those rules, and re-deriving them here would
 * be a second copy of an authorization decision. Either absence means "not offerable", so the intersection
 * is the answer.
 */
export interface BookableDoctor {
  id: string;
  name: { en: string; ar: string };
  /** The primary specialty code. Always present — a doctor without one cannot be reached by this path. */
  specialtyCode: string;
  openSlots: number;
  nextSlotStart: string;
}

export function bookableDoctors(
  practitioners: Practitioner[],
  availability: DoctorAvailability[],
): BookableDoctor[] {
  const openBy = new Map(availability.map((a) => [a.doctorId, a]));

  return practitioners
    .flatMap((p) => {
      const open = openBy.get(p.id);
      if (!open) return [];
      // No primary specialty means the specialty filter above this cannot place them in any group, so they
      // would sit in a picker that no specialty selection ever reveals. The practitioner admin screen flags
      // exactly this record as "not bookable"; honouring that here keeps the two screens telling one story.
      if (!p.primarySpecialty) return [];
      return [{
        id: p.id,
        name: p.name,
        specialtyCode: p.primarySpecialty,
        openSlots: open.openSlots,
        nextSlotStart: open.nextSlotStart,
      }];
    })
    // Soonest first: "who can see this patient today" is the question actually being asked at the desk.
    .sort((a, b) => a.nextSlotStart.localeCompare(b.nextSlotStart));
}

/**
 * The specialties worth offering at a branch — those with at least one bookable doctor behind them.
 *
 * Derived rather than fetched whole, for the reason `/branch-clinics` gives about clinics: a specialty listed
 * with nothing behind it is a dead end the operator only discovers after choosing it, and at a desk with a
 * patient waiting that is a real cost. Ordered by the reference list so the dropdown is stable between loads.
 */
export function availableSpecialties(doctors: BookableDoctor[], reference: Specialty[]): Specialty[] {
  const present = new Set(doctors.map((d) => d.specialtyCode));
  return reference.filter((s) => present.has(s.code));
}
