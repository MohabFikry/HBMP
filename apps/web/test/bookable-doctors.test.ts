import { describe, expect, it } from "vitest";
import type { DoctorAvailability, Practitioner, Specialty } from "@mersal/contracts";
import { availableSpecialties, bookableDoctors } from "../src/screens/booking/bookableDoctors";

const chip = { kind: "ok" as const, label: { en: "Active", ar: "نشط" } };

function doctor(id: string, name: string, primary?: string): Practitioner {
  return {
    id,
    practitionerType: "Doctor",
    name: { en: name, ar: name },
    primarySpecialty: primary,
    specialties: primary ? [primary] : [],
    branches: ["BR-DOK"],
    status: chip,
  };
}

function open(doctorId: string, openSlots: number, nextSlotStart: string): DoctorAvailability {
  return { doctorId, branchId: "BR-DOK", openSlots, nextSlotStart };
}

const REFERENCE: Specialty[] = [
  { code: "GP", name: { en: "General Practice", ar: "الممارسة العامة" } },
  { code: "PED", name: { en: "Pediatrics", ar: "طب الأطفال" } },
  { code: "CARD", name: { en: "Cardiology", ar: "أمراض القلب" } },
];

/**
 * The join exists because no single service may answer "name, specialty AND availability" — provider-service
 * owns the first two, emr the third, and neither may read the other's data on the caller's behalf. What is
 * worth testing is the consequence of that split: a doctor must be dropped when EITHER side omits them.
 */
describe("bookableDoctors — joining provider-service identity with emr availability", () => {
  it("keeps only doctors both sides agree on", () => {
    const practitioners = [
      doctor("d1", "Hana Mansour", "PED"),
      doctor("d2", "Mona Saleh", "CARD"),   // no availability — a full calendar
      doctor("d3", "Youssef Adel", "GP"),
    ];
    const availability = [
      open("d1", 6, "2026-07-30T09:00:00Z"),
      open("d3", 2, "2026-07-30T08:00:00Z"),
      open("d9", 5, "2026-07-30T07:00:00Z"),   // slots for someone provider-service did not return
    ];

    const result = bookableDoctors(practitioners, availability);

    expect(result.map((d) => d.id)).toEqual(["d3", "d1"]);
    // d2 is a real, active doctor with a full calendar: offering them leads to a "pick a time" step with no
    // times in it.
    expect(result.find((d) => d.id === "d2")).toBeUndefined();
    // d9 has slots but provider-service did not return them for this branch/specialty — that omission IS an
    // authorization decision, and re-deriving it here would be a second copy of it.
    expect(result.find((d) => d.id === "d9")).toBeUndefined();
  });

  it("drops a doctor with no primary specialty rather than showing them under none", () => {
    // The same record the practitioner admin screen flags "not bookable". The two screens must agree.
    const result = bookableDoctors([doctor("d4", "Karim Fouad", undefined)], [open("d4", 3, "2026-07-30T09:00:00Z")]);
    expect(result).toEqual([]);
  });

  it("orders by the soonest open slot, not by name", () => {
    const practitioners = [doctor("d1", "Aaa", "PED"), doctor("d2", "Zzz", "GP")];
    const availability = [
      open("d1", 1, "2026-08-02T09:00:00Z"),
      open("d2", 1, "2026-07-30T09:00:00Z"),
    ];
    // "Who can see this patient soonest" is the question at the desk.
    expect(bookableDoctors(practitioners, availability).map((d) => d.id)).toEqual(["d2", "d1"]);
  });

  it("carries the open-slot count through, so the picker can show it", () => {
    const result = bookableDoctors([doctor("d1", "Hana", "PED")], [open("d1", 7, "2026-07-30T09:00:00Z")]);
    expect(result[0].openSlots).toBe(7);
  });
});

describe("availableSpecialties", () => {
  it("offers only specialties with a bookable doctor behind them", () => {
    const doctors = bookableDoctors(
      [doctor("d1", "Hana", "PED"), doctor("d2", "Mona", "CARD")],
      [open("d1", 4, "2026-07-30T09:00:00Z")],   // only PED has availability
    );

    const offered = availableSpecialties(doctors, REFERENCE);

    expect(offered.map((s) => s.code)).toEqual(["PED"]);
    // Cardiology exists in the reference set and has a doctor — but no bookable one, so choosing it would
    // present an empty doctor list.
    expect(offered.find((s) => s.code === "CARD")).toBeUndefined();
  });

  it("returns them in reference order, not in availability order", () => {
    // A dropdown that reshuffles between loads is one an operator cannot build muscle memory for.
    const doctors = bookableDoctors(
      [doctor("d1", "A", "CARD"), doctor("d2", "B", "GP")],
      [open("d1", 1, "2026-07-30T10:00:00Z"), open("d2", 1, "2026-07-30T09:00:00Z")],
    );
    expect(doctors.map((d) => d.specialtyCode)).toEqual(["GP", "CARD"]);        // soonest-first
    expect(availableSpecialties(doctors, REFERENCE).map((s) => s.code)).toEqual(["GP", "CARD"]);
  });

  it("is empty when nothing is bookable", () => {
    expect(availableSpecialties([], REFERENCE)).toEqual([]);
  });
});
