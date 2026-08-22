import type {
  AvailabilityApi, AvailabilityHistoryEntry, AvailabilityRule, BranchApi, BranchApis, BranchPractitioner,
  BranchRef, CreateRosterExceptionBody, InventoryApi, RosterApi, RosterException, RosterHistoryEntry,
  PractitionerHistoryEntry,
} from "../api/branchApi";

/**
 * The demo backend for the Clinic Management portal.
 *
 * <b>Why it exists.</b> `branchApi` and its siblings were the only surface on the platform that called
 * `http.ts` directly instead of resolving through a swappable client. The SPA runs in fixture mode by
 * default and there is no MSW anywhere in the tree, so all five branch screens errored in the demo bundle
 * and none of them could be rendered in a test. That is why this portal's only test exercised a status chip
 * in isolation, and why the axe route sweep skipped these routes while reporting itself complete.
 *
 * <b>Consistency with `DevApiClient` is the point.</b> The ids and names below are the same six clinics and
 * the same practitioners the rest of the demo uses. Two fixture sets that disagree are worse than one that is
 * thin: a demo where the roster names a doctor the booking screen has never heard of teaches the reader that
 * the data is meaningless, and they stop reading it.
 *
 * Latency matches `DevApiClient`'s 250ms so the loading states the screens are built around actually appear.
 */

const LATENCY_MS = 250;

const after = <T>(value: T): Promise<T> =>
  new Promise((resolve) => setTimeout(() => resolve(value), LATENCY_MS));

/** The six seeded clinics (provider/0005_branch.sql). */
const BRANCHES: BranchRef[] = [
  { branchId: "b1000000-0000-4000-8000-000000000001", branchCode: "MAA", nameEn: "Maadi", nameAr: "المعادي" },
  { branchId: "b1000000-0000-4000-8000-000000000002", branchCode: "DOK", nameEn: "Dokki", nameAr: "الدقي" },
  { branchId: "b1000000-0000-4000-8000-000000000003", branchCode: "NSR", nameEn: "Nasr City", nameAr: "مدينة نصر" },
  { branchId: "b1000000-0000-4000-8000-000000000004", branchCode: "OCT", nameEn: "6th October", nameAr: "السادس من أكتوبر" },
  { branchId: "b1000000-0000-4000-8000-000000000005", branchCode: "ALX", nameEn: "Alexandria", nameAr: "الإسكندرية" },
  { branchId: "b1000000-0000-4000-8000-000000000006", branchCode: "ASW", nameEn: "Aswan", nameAr: "أسوان" },
];

const MAADI = BRANCHES[0].branchId;
const DOKKI = BRANCHES[1].branchId;

const iso = (daysFromNow: number): string => {
  const d = new Date();
  d.setDate(d.getDate() + daysFromNow);
  return d.toISOString().slice(0, 10);
};

const stamp = (daysAgo: number): string => {
  const d = new Date();
  d.setDate(d.getDate() - daysAgo);
  return d.toISOString();
};

/**
 * Four clinicians spanning the four licence states the screen must distinguish — valid, expiring, expired,
 * and never recorded. A fixture where everything is fine demonstrates nothing: the states that need action
 * are the ones the screen exists for, and they have to be visible without anybody editing data first.
 */
const PRACTITIONERS: BranchPractitioner[] = [
  {
    practitionerId: "d0000000-0000-4000-8000-000000000001",
    practitionerType: "Doctor", fullNameEn: "Hala Fouad", fullNameAr: "هالة فؤاد",
    primarySpecialty: "General Practice", specialties: ["GP"], branches: [MAADI],
    status: "Active", licenseNo: "EG-DOC-44182", licenseExpiry: iso(420),
    licenceValid: true, daysUntilExpiry: 420,
  },
  {
    practitionerId: "d0000000-0000-4000-8000-000000000002",
    practitionerType: "Doctor", fullNameEn: "Karim Adel", fullNameAr: "كريم عادل",
    primarySpecialty: "Cardiology", specialties: ["CARD"], branches: [MAADI, DOKKI],
    status: "Active", licenseNo: "EG-DOC-51907", licenseExpiry: iso(24),
    licenceValid: true, daysUntilExpiry: 24,
  },
  {
    practitionerId: "d0000000-0000-4000-8000-000000000003",
    practitionerType: "Doctor", fullNameEn: "Nadia Rashed", fullNameAr: "نادية راشد",
    primarySpecialty: "Paediatrics", specialties: ["PAED"], branches: [DOKKI],
    status: "Active", licenseNo: "EG-DOC-33025", licenseExpiry: iso(-11),
    licenceValid: false, daysUntilExpiry: -11,
  },
  {
    practitionerId: "n0000000-0000-4000-8000-000000000001",
    practitionerType: "Nurse", fullNameEn: "Mona Saleh", fullNameAr: "منى صالح",
    primarySpecialty: null, specialties: [], branches: [MAADI],
    // Never recorded is NOT expired, and the screen renders them differently. A nurse with no licence number
    // must not appear in the renewal queue.
    status: "Active", licenseNo: null, licenseExpiry: null,
    licenceValid: null, daysUntilExpiry: null,
  },
];

// Mutable so the demo behaves like an application rather than a screenshot: a renewal recorded on the
// licence screen is visible on the alerts screen, and an exception applied on the roster appears in its list.
const practitioners = PRACTITIONERS.map((p) => ({ ...p }));

let exceptions: RosterException[] = [
  {
    exceptionId: "e0000000-0000-4000-8000-000000000001",
    branchId: MAADI, practitionerId: null,
    dateFrom: iso(12), dateTo: iso(12), kind: "PublicHoliday",
    startTime: null, endTime: null, reason: "Eid al-Adha", wholeDay: true, subtractive: true,
    createdAt: stamp(20), createdBy: "u-coordinator",
  },
  {
    exceptionId: "e0000000-0000-4000-8000-000000000002",
    branchId: MAADI, practitionerId: PRACTITIONERS[0].practitionerId,
    dateFrom: iso(4), dateTo: iso(6), kind: "Leave",
    startTime: null, endTime: null, reason: "Annual leave", wholeDay: true, subtractive: true,
    createdAt: stamp(3), createdBy: "u-coordinator",
  },
];

let rules: AvailabilityRule[] = [
  {
    availabilityId: "a0000000-0000-4000-8000-000000000001",
    providerId: "p0000000-0000-4000-8000-000000000001",
    locationId: "l0000000-0000-4000-8000-000000000001",
    branchId: MAADI, doctorId: PRACTITIONERS[0].practitionerId,
    dayOfWeek: 2, startTime: "09:00", endTime: "13:00", slotMinutes: 15,
    maxPerDay: 12, slotsFromWindow: 16, slotsPerDay: 12,
    updatedAt: stamp(9), updatedBy: "u-coordinator", updatedByName: "Mona Saleh",
  },
  {
    availabilityId: "a0000000-0000-4000-8000-000000000002",
    providerId: "p0000000-0000-4000-8000-000000000001",
    locationId: "l0000000-0000-4000-8000-000000000001",
    branchId: MAADI, doctorId: PRACTITIONERS[1].practitionerId,
    dayOfWeek: 4, startTime: "14:00", endTime: "18:00", slotMinutes: 20,
    // Uncapped, which is what every rule predating the cap looks like — the screen has to read well for both.
    maxPerDay: null, slotsFromWindow: 12, slotsPerDay: 12,
    updatedAt: stamp(31), updatedBy: "u-network", updatedByName: "Network Team",
  },
  /*
    33.10 — A WEEK WITH SOMETHING IN IT.

    Two rules were enough to demonstrate the pattern TABLE and not enough to demonstrate anything else: five
    of seven weekdays were empty, so the day view had nothing to show on most dates, and no clinician
    appeared at two clinics — the one case the pattern pane exists to disambiguate. These add the rest of Dr
    Karim's week across Maadi AND Dokki, and give Dr Nadia a Dokki pattern, so the demo shows a clinician
    whose Tuesday and Wednesday are in different buildings.
  */
  {
    availabilityId: "a0000000-0000-4000-8000-000000000003",
    providerId: "p0000000-0000-4000-8000-000000000001",
    locationId: "l0000000-0000-4000-8000-000000000001",
    branchId: MAADI, doctorId: PRACTITIONERS[0].practitionerId,
    dayOfWeek: 0, startTime: "09:00", endTime: "13:00", slotMinutes: 15,
    maxPerDay: 12, slotsFromWindow: 16, slotsPerDay: 12,
    updatedAt: stamp(9), updatedBy: "u-coordinator", updatedByName: "Mona Saleh",
  },
  {
    availabilityId: "a0000000-0000-4000-8000-000000000004",
    providerId: "p0000000-0000-4000-8000-000000000001",
    locationId: "l0000000-0000-4000-8000-000000000001",
    branchId: MAADI, doctorId: PRACTITIONERS[1].practitionerId,
    dayOfWeek: 1, startTime: "14:00", endTime: "18:00", slotMinutes: 20,
    maxPerDay: null, slotsFromWindow: 12, slotsPerDay: 12,
    updatedAt: stamp(31), updatedBy: "u-network", updatedByName: "Network Team",
  },
  {
    availabilityId: "a0000000-0000-4000-8000-000000000005",
    providerId: "p0000000-0000-4000-8000-000000000002",
    locationId: "l0000000-0000-4000-8000-000000000002",
    branchId: DOKKI, doctorId: PRACTITIONERS[1].practitionerId,
    dayOfWeek: 3, startTime: "14:00", endTime: "17:00", slotMinutes: 20,
    maxPerDay: 8, slotsFromWindow: 9, slotsPerDay: 8,
    updatedAt: stamp(12), updatedBy: "u-manager", updatedByName: "Clinics Manager",
  },
  {
    availabilityId: "a0000000-0000-4000-8000-000000000006",
    providerId: "p0000000-0000-4000-8000-000000000002",
    locationId: "l0000000-0000-4000-8000-000000000002",
    branchId: DOKKI, doctorId: PRACTITIONERS[2].practitionerId,
    dayOfWeek: 0, startTime: "10:00", endTime: "14:00", slotMinutes: 30,
    maxPerDay: null, slotsFromWindow: 8, slotsPerDay: 8,
    updatedAt: stamp(60), updatedBy: "u-network", updatedByName: "Network Team",
  },
  {
    availabilityId: "a0000000-0000-4000-8000-000000000007",
    providerId: "p0000000-0000-4000-8000-000000000002",
    locationId: "l0000000-0000-4000-8000-000000000002",
    branchId: DOKKI, doctorId: PRACTITIONERS[2].practitionerId,
    dayOfWeek: 3, startTime: "10:00", endTime: "14:00", slotMinutes: 30,
    maxPerDay: null, slotsFromWindow: 8, slotsPerDay: 8,
    updatedAt: stamp(60), updatedBy: "u-network", updatedByName: "Network Team",
  },
];

/** Ids for rules the demo CREATES. Counted from past the seeded block so a new rule cannot reuse one. */
let ruleSeq = 100;

const availabilityHistory: Record<string, AvailabilityHistoryEntry[]> = {
  "a0000000-0000-4000-8000-000000000001": [
    {
      sequence: 1, operation: "INSERT", recordedAt: stamp(40), actorSubject: "u-network",
      actorName: "Network Team", startTime: "09:00", endTime: "12:00", slotMinutes: 15,
      maxPerDay: null, retired: false,
    },
    {
      sequence: 2, operation: "UPDATE", recordedAt: stamp(21), actorSubject: "u-coordinator",
      actorName: "Mona Saleh", startTime: "09:00", endTime: "13:00", slotMinutes: 15,
      maxPerDay: null, retired: false,
    },
    {
      sequence: 3, operation: "UPDATE", recordedAt: stamp(9), actorSubject: "u-coordinator",
      actorName: "Mona Saleh", startTime: "09:00", endTime: "13:00", slotMinutes: 15,
      maxPerDay: 12, retired: false,
    },
  ],
};

const slotsFromWindow = (start: string, end: string, minutes: number): number => {
  const toMin = (t: string): number => {
    const [h, m] = t.split(":").map(Number);
    return h * 60 + m;
  };
  const span = toMin(end) - toMin(start);
  return span > 0 && minutes > 0 ? Math.floor(span / minutes) : 0;
};

/**
 * The rule's own fields, spelled out rather than derived with `Omit<AvailabilityRule, …>`.
 *
 * `AvailabilityRule` is inferred from a `.passthrough()` schema, so it carries an index signature — and
 * `Omit` over an index signature widens every remaining property to `{}`. The result type-checks nowhere
 * useful and the errors point at the arithmetic rather than at the type.
 */
interface RuleFields {
  availabilityId: string;
  providerId: string;
  locationId: string;
  branchId: string | null;
  doctorId: string | null;
  dayOfWeek: number;
  startTime: string;
  endTime: string;
  slotMinutes: number;
  maxPerDay: number | null;
  updatedAt: string | null;
  updatedBy: string | null;
  updatedByName: string | null;
}

const withCounts = (r: RuleFields): AvailabilityRule => {
  const fromWindow = slotsFromWindow(r.startTime, r.endTime, r.slotMinutes);
  return {
    ...r,
    slotsFromWindow: fromWindow,
    slotsPerDay: r.maxPerDay !== null && r.maxPerDay < fromWindow ? r.maxPerDay : fromWindow,
  };
};

/** The affected-appointment list a preview returns. Named people, because that is what the list is FOR. */
const AFFECTED = [
  { appointmentId: "ap000000-0000-4000-8000-000000000001", beneficiaryId: "be000000-0000-4000-8000-000000000001", beneficiaryName: "Amal Hassan", branchId: MAADI, doctorId: PRACTITIONERS[0].practitionerId, scheduledStart: `${iso(5)}T09:30:00Z` },
  { appointmentId: "ap000000-0000-4000-8000-000000000002", beneficiaryId: "be000000-0000-4000-8000-000000000002", beneficiaryName: "Youssef Ibrahim", branchId: MAADI, doctorId: PRACTITIONERS[0].practitionerId, scheduledStart: `${iso(5)}T10:15:00Z` },
  { appointmentId: "ap000000-0000-4000-8000-000000000003", beneficiaryId: "be000000-0000-4000-8000-000000000003", beneficiaryName: "Sara Mahmoud", branchId: MAADI, doctorId: PRACTITIONERS[0].practitionerId, scheduledStart: `${iso(6)}T11:00:00Z` },
];

const devBranchApi: BranchApi = {
  practitioners: (params = {}) =>
    after(practitioners.filter((p) => {
      if (params.branchId && !p.branches.includes(params.branchId)) return false;
      // The picker hides unlicensed clinicians; the coordinator's roster must SHOW them, because those are
      // precisely the records needing action.
      if (!params.includeUnlicensed && p.licenceValid === false) return false;
      return true;
    }).map((p) => ({ ...p }))),

  licenceAlerts: (withinDays = 90) =>
    after({
      asOf: new Date().toISOString(),
      withinDays,
      alerts: practitioners
        .filter((p) => p.daysUntilExpiry !== null && p.daysUntilExpiry <= withinDays)
        .map((p) => ({
          practitionerId: p.practitionerId, fullNameEn: p.fullNameEn, fullNameAr: p.fullNameAr,
          practitionerType: p.practitionerType, licenseNo: p.licenseNo, licenseExpiry: p.licenseExpiry,
          daysUntilExpiry: p.daysUntilExpiry,
          // Named by the server, never derived on the client from a negative number.
          status: (p.daysUntilExpiry ?? 0) < 0 ? "Expired" : "Expiring",
          branches: p.branches,
        })),
    }),

  updateLicence: (practitionerId, body) => {
    const p = practitioners.find((x) => x.practitionerId === practitionerId);
    if (p) {
      p.licenseNo = body.licenseNo;
      p.licenseExpiry = body.licenseExpiry;
      const days = Math.round(
        (new Date(`${body.licenseExpiry}T00:00:00Z`).getTime() - Date.now()) / 86_400_000);
      p.daysUntilExpiry = days;
      p.licenceValid = days >= 0;
    }
    return after({ practitionerId, licenseNo: body.licenseNo, licenseExpiry: body.licenseExpiry });
  },

  licenceImpact: (practitionerId, expiry) => {
    // Only what falls BEYOND the proposed date — inclusive of the day printed on the certificate, matching
    // the server. A demo that got this boundary wrong would teach the wrong rule.
    const cutoff = new Date(`${expiry}T23:59:59Z`).getTime();
    const affected = AFFECTED.filter(
      (a) => a.doctorId === practitionerId && new Date(a.scheduledStart).getTime() > cutoff);
    return after({
      asOf: new Date().toISOString(),
      doctorId: practitionerId,
      proposedExpiry: expiry,
      affectedCount: affected.length,
      affected,
    });
  },

  practitionerHistory: (practitionerId) => {
    const p = practitioners.find((x) => x.practitionerId === practitionerId);
    const entries: PractitionerHistoryEntry[] = [
      {
        sequence: 1, operation: "INSERT", recordedAt: stamp(400), actorSubject: "u-network",
        actorName: "Network Team", licenseNo: p?.licenseNo ?? null,
        licenseExpiry: iso(-370), status: "Active", deleted: false,
      },
      {
        sequence: 2, operation: "UPDATE", recordedAt: stamp(35), actorSubject: "u-coordinator",
        actorName: "Mona Saleh", licenseNo: p?.licenseNo ?? null,
        licenseExpiry: p?.licenseExpiry ?? null, status: "Active", deleted: false,
      },
    ];
    return after({ practitionerId, entries });
  },

  assignBranch: (practitionerId, body) => after({ practitionerId, branchId: body.branchId }),

  reassignmentNeeded: () =>
    after({
      asOf: new Date().toISOString(),
      count: 2,
      appointments: AFFECTED.slice(0, 2).map((a) => ({
        appointmentId: a.appointmentId, beneficiaryId: a.beneficiaryId, branchId: a.branchId,
        doctorId: a.doctorId, scheduledStart: a.scheduledStart,
        scheduledEnd: a.scheduledStart, status: "Booked",
        reassignmentNeededAt: stamp(2), beneficiaryName: a.beneficiaryName,
      })),
    }),

  branches: () => after(BRANCHES.map((b) => ({ ...b }))),
};

const devRosterApi: RosterApi = {
  list: (params = {}) =>
    after(exceptions
      .filter((e) => !params.branchId || e.branchId === params.branchId || e.branchId === null)
      .filter((e) => !params.practitionerId || e.practitionerId === params.practitionerId)
      .map((e) => ({ ...e }))),

  preview: (body: CreateRosterExceptionBody) => {
    // The affected list depends on WHO and WHERE, so the demo shows the preview doing its job rather than
    // always returning the same three names.
    const affected = body.practitionerId
      ? AFFECTED.filter((a) => a.doctorId === body.practitionerId)
      : AFFECTED;
    return after({ dryRun: true as const, affectedCount: affected.length, affected });
  },

  apply: (body: CreateRosterExceptionBody) => {
    const exceptionId = `e0000000-0000-4000-8000-${String(exceptions.length + 3).padStart(12, "0")}`;
    exceptions = [...exceptions, {
      exceptionId,
      branchId: body.branchId ?? null,
      practitionerId: body.practitionerId ?? null,
      dateFrom: body.dateFrom, dateTo: body.dateTo, kind: body.kind,
      startTime: body.startTime ?? null, endTime: body.endTime ?? null,
      reason: body.reason,
      wholeDay: !body.startTime && !body.endTime,
      subtractive: body.kind !== "AdHocClinic",
      createdAt: new Date().toISOString(), createdBy: "u-coordinator",
    }];
    const affectedCount = body.acknowledgedImpactCount ?? 0;
    // FLAGGED, never cancelled — and the demo says so in the numbers, not only in the copy.
    return after({ exceptionId, affectedCount, flagged: affectedCount, cancelled: 0 });
  },

  withdraw: (exceptionId) => {
    exceptions = exceptions.filter((e) => e.exceptionId !== exceptionId);
    return after({ exceptionId, withdrawn: true });
  },

  /**
   * 33.10 — the demo's day roster.
   *
   * <p>A MIRROR of a server computation, and knowingly so. On a real deployment this comes out of
   * `SlotGeneration` — the one place availability is decided — and the browser never re-derives it. The
   * fixture has no server to ask, so it reproduces the four rules the endpoint applies: a whole-day
   * subtraction removes the day outright (an extra clinic at a shut clinic is not a clinic), a part-day one
   * removes only the slots it overlaps, the cap applies after subtraction, and a trailing partial slot is
   * not a slot.</p>
   *
   * <p>It is worth writing out rather than faking, because a demo whose numbers do not behave is a demo that
   * teaches the reader the numbers are decoration.</p>
   */
  day: ({ branchId, date }) => {
    const dayOfWeek = new Date(`${date}T00:00:00Z`).getUTCDay();

    const onDate = exceptions.filter(
      (e) => e.dateFrom <= date && date <= e.dateTo
        && (e.branchId === null || !branchId || e.branchId === branchId));

    const bites = (e: RosterException, ruleBranch: string | null, ruleDoctor: string | null): boolean =>
      (e.branchId === null || e.branchId === ruleBranch)
      && (e.practitionerId === null || e.practitionerId === ruleDoctor);

    const minutes = (t: string): number => {
      const [h, m] = t.split(":").map(Number);
      return h * 60 + m;
    };

    /** Slots surviving the part-day subtractions, then the cap. Overlap, not containment. */
    const surviving = (
      start: string, end: string, slotMinutes: number, cap: number | null,
      cuts: RosterException[],
    ): number => {
      if (slotMinutes <= 0) return 0;
      let count = 0;
      for (let t = minutes(start); t + slotMinutes <= minutes(end); t += slotMinutes) {
        const covered = cuts.some((c) =>
          c.wholeDay
          || (t < minutes(c.endTime ?? "24:00") && t + slotMinutes > minutes(c.startTime ?? "00:00")));
        if (covered) continue;
        count += 1;
        if (cap !== null && count >= cap) break;
      }
      return count;
    };

    const lines = rules
      .filter((r) => r.dayOfWeek === dayOfWeek && (!branchId || r.branchId === branchId))
      .map((r) => {
        const applicable = onDate.filter((e) => bites(e, r.branchId, r.doctorId));
        const cuts = applicable.filter((e) => e.subtractive);
        const offered = cuts.some((c) => c.wholeDay)
          ? 0
          : surviving(r.startTime, r.endTime, r.slotMinutes, r.maxPerDay, cuts);
        const blocking = cuts[0] ?? null;
        return {
          availabilityId: r.availabilityId, practitionerId: r.doctorId, branchId: r.branchId,
          startTime: r.startTime, endTime: r.endTime, slotMinutes: r.slotMinutes, maxPerDay: r.maxPerDay,
          slotsFromPattern: r.slotsPerDay, slotsOffered: offered,
          // Demo bookings: a plausible load rather than an empty clinic, and never more than exist.
          booked: Math.min(offered, (r.slotMinutes + dayOfWeek) % 5),
          status: offered === 0 && blocking ? "Off" : "Working",
          exceptionKind: blocking?.kind ?? null,
          exceptionReason: blocking?.reason ?? null,
        };
      });

    const extra = onDate
      .filter((e) => e.kind === "AdHocClinic")
      // A whole-day closure outranks an extra session — the generator's own precedence.
      .filter((e) => !onDate.some((c) => c.subtractive && c.wholeDay && bites(c, e.branchId, e.practitionerId)))
      .filter((e) => !rules.some((r) => r.dayOfWeek === dayOfWeek
        && r.doctorId === e.practitionerId && r.branchId === e.branchId))
      .map((e) => {
        const template = rules.find((r) => r.doctorId === e.practitionerId && r.branchId === e.branchId);
        const slotMinutes = template?.slotMinutes ?? 0;
        const offered = surviving(e.startTime ?? "00:00", e.endTime ?? "23:59", slotMinutes, template?.maxPerDay ?? null, []);
        return {
          availabilityId: null, practitionerId: e.practitionerId, branchId: e.branchId,
          startTime: e.startTime ?? "00:00", endTime: e.endTime ?? "23:59",
          slotMinutes, maxPerDay: template?.maxPerDay ?? null,
          slotsFromPattern: offered, slotsOffered: offered, booked: 0,
          status: "Extra", exceptionKind: e.kind, exceptionReason: e.reason,
        };
      });

    const all = [...lines, ...extra].sort((a, b) => a.startTime.localeCompare(b.startTime));
    const slotsOffered = all.reduce((n, l) => n + l.slotsOffered, 0);
    const booked = all.reduce((n, l) => n + l.booked, 0);

    return after({
      date,
      branchId: branchId ?? null,
      lines: all,
      notices: onDate.map((e) => ({
        exceptionId: e.exceptionId, kind: e.kind, reason: e.reason,
        branchId: e.branchId, practitionerId: e.practitionerId,
        wholeDay: e.wholeDay, startTime: e.startTime, endTime: e.endTime, subtractive: e.subtractive,
      })),
      summary: {
        clinicians: all.filter((l) => l.status !== "Off").length,
        slotsOffered, booked, open: Math.max(0, slotsOffered - booked),
      },
    });
  },

  history: (exceptionId) => {
    const e = exceptions.find((x) => x.exceptionId === exceptionId);
    const entries: RosterHistoryEntry[] = [{
      sequence: 1, operation: "INSERT", recordedAt: e?.createdAt ?? stamp(1),
      actorSubject: e?.createdBy ?? null, kind: e?.kind ?? null,
      dateFrom: e?.dateFrom ?? null, dateTo: e?.dateTo ?? null,
      startTime: e?.startTime ?? null, endTime: e?.endTime ?? null,
      reason: e?.reason ?? null, withdrawn: false,
    }];
    return after({ exceptionId, entries });
  },
};

const devAvailabilityApi: AvailabilityApi = {
  list: (params = {}) =>
    after(rules
      .filter((r) => !params.branchId || r.branchId === params.branchId)
      .filter((r) => !params.doctorId || r.doctorId === params.doctorId)
      .map((r) => ({ ...r }))),

  create: (body) => {
    const rule = withCounts({
      availabilityId: `a0000000-0000-4000-8000-${String(++ruleSeq).padStart(12, "0")}`,
      providerId: body.providerId, locationId: body.locationId,
      branchId: body.branchId ?? null, doctorId: body.doctorId ?? null,
      dayOfWeek: body.dayOfWeek, startTime: body.startTime, endTime: body.endTime,
      slotMinutes: body.slotMinutes, maxPerDay: body.maxPerDay ?? null,
      updatedAt: new Date().toISOString(), updatedBy: "u-coordinator", updatedByName: "Mona Saleh",
    });
    rules = [...rules, rule];
    return after({ ...rule });
  },

  update: (availabilityId, body) => {
    const updated = withCounts({
      availabilityId,
      providerId: body.providerId, locationId: body.locationId,
      branchId: body.branchId ?? null, doctorId: body.doctorId ?? null,
      dayOfWeek: body.dayOfWeek, startTime: body.startTime, endTime: body.endTime,
      slotMinutes: body.slotMinutes, maxPerDay: body.maxPerDay ?? null,
      updatedAt: new Date().toISOString(), updatedBy: "u-coordinator", updatedByName: "Mona Saleh",
    });
    rules = rules.map((r) => (r.availabilityId === availabilityId ? updated : r));
    return after({ ...updated });
  },

  retire: (availabilityId) => {
    rules = rules.filter((r) => r.availabilityId !== availabilityId);
    return after({ availabilityId, retired: true });
  },

  history: (availabilityId) =>
    after({ availabilityId, entries: availabilityHistory[availabilityId] ?? [] }),
};

/**
 * Clinic stock.
 *
 * Given real fixtures rather than left pointing at the HTTP client, for two reasons. The practical one is
 * that `BranchApis` resolves all four surfaces together precisely so a half-supplied fixture is impossible —
 * a portal where four screens work and the fifth throws is the state this whole change exists to end. The
 * mechanical one is that delegating here imports `branchApi.ts` back, and `branchApi.ts` imports this module
 * through `@dev/fixtures`: the cycle leaves the binding undefined at module-eval time, which surfaces as a
 * `TypeError` from a file that looks like it is only defining constants.
 *
 * The rows cover what the screen exists to surface — low stock, a batch nearing expiry, and an expired
 * medical batch under quarantine — because stock that is simply fine demonstrates nothing.
 */
const ITEMS = {
  gloves: { itemId: "i0000000-0000-4000-8000-000000000001", sku: "CON-GLV-M", nameEn: "Examination gloves (M)", nameAr: "قفازات فحص (وسط)", category: "Medical" as const, unitOfMeasure: "box", coldChain: false },
  sutures: { itemId: "i0000000-0000-4000-8000-000000000002", sku: "CON-SUT-30", nameEn: "Sutures 3-0", nameAr: "خيوط جراحية ٣-٠", category: "Medical" as const, unitOfMeasure: "pack", coldChain: false },
  reagent: { itemId: "i0000000-0000-4000-8000-000000000003", sku: "LAB-RGT-CBC", nameEn: "CBC reagent", nameAr: "كاشف صورة دم كاملة", category: "Medical" as const, unitOfMeasure: "vial", coldChain: true },
  paper: { itemId: "i0000000-0000-4000-8000-000000000004", sku: "OFF-PPR-A4", nameEn: "A4 paper", nameAr: "ورق A4", category: "NonMedical" as const, unitOfMeasure: "ream", coldChain: false },
};

const STOCK = [
  { ...ITEMS.gloves, branchId: MAADI, batchId: "bt000000-0000-4000-8000-000000000001", batchNo: "GLV-2451", expiryDate: iso(300), onHand: 42, reorderLevel: 20, isLow: false, isQuarantined: false },
  { ...ITEMS.sutures, branchId: MAADI, batchId: "bt000000-0000-4000-8000-000000000002", batchNo: "SUT-1180", expiryDate: iso(55), onHand: 6, reorderLevel: 15, isLow: true, isQuarantined: false },
  { ...ITEMS.reagent, branchId: MAADI, batchId: "bt000000-0000-4000-8000-000000000003", batchNo: "RGT-0912", expiryDate: iso(-8), onHand: 4, reorderLevel: 5, isLow: true, isQuarantined: true },
  { ...ITEMS.paper, branchId: MAADI, batchId: null, batchNo: null, expiryDate: null, onHand: 30, reorderLevel: 10, isLow: false, isQuarantined: false },
];

const devInventoryApi: InventoryApi = {
  stock: (params = {}) =>
    after({
      asOf: new Date().toISOString(),
      branches: [MAADI],
      stock: STOCK
        .filter((s) => !params.branchId || s.branchId === params.branchId)
        .filter((s) => !params.category || s.category === params.category)
        .filter((s) => !params.lowStock || s.isLow)
        .map((s) => ({ ...s })),
    }),

  movements: (params = {}) =>
    after({
      total: 3, page: params.page ?? 1, pageSize: params.pageSize ?? 25,
      movements: [
        // SIGNED quantities: on-hand is their sum, and there is no stored balance anywhere.
        { movementId: "mv000000-0000-4000-8000-000000000001", branchId: MAADI, itemId: ITEMS.gloves.itemId, batchId: "bt000000-0000-4000-8000-000000000001", kind: "Receipt" as const, quantity: 50, reason: "Monthly delivery", transferRef: null, counterpartyBranchId: null, actor: "u-storekeeper", occurredAt: stamp(14) },
        { movementId: "mv000000-0000-4000-8000-000000000002", branchId: MAADI, itemId: ITEMS.gloves.itemId, batchId: "bt000000-0000-4000-8000-000000000001", kind: "Issue" as const, quantity: -8, reason: "Clinic use", transferRef: null, counterpartyBranchId: null, actor: "u-nurse", occurredAt: stamp(6) },
        { movementId: "mv000000-0000-4000-8000-000000000003", branchId: MAADI, itemId: ITEMS.sutures.itemId, batchId: "bt000000-0000-4000-8000-000000000002", kind: "Count" as const, quantity: -2, reason: "Stock-take variance", transferRef: null, counterpartyBranchId: null, actor: "u-storekeeper", occurredAt: stamp(2) },
      ],
    }),

  alerts: () =>
    after({
      asOf: new Date().toISOString(),
      branches: [MAADI],
      lowStock: STOCK.filter((s) => s.isLow).map((s) => ({
        branchId: s.branchId, itemId: s.itemId, name: s.nameEn, onHand: s.onHand,
        reorderLevel: s.reorderLevel, leadTimeDays: 7,
      })),
      expiring: STOCK
        .filter((s) => s.expiryDate !== null && !s.isQuarantined)
        .map((s) => ({
          branchId: s.branchId, itemId: s.itemId, batchId: s.batchId!, batchNo: s.batchNo!,
          expiryDate: s.expiryDate!, name: s.nameEn, onHand: s.onHand,
          daysRemaining: Math.round((new Date(s.expiryDate!).getTime() - Date.now()) / 86_400_000),
          quarantined: false,
        }))
        .filter((s) => s.daysRemaining <= 90),
      quarantined: STOCK.filter((s) => s.isQuarantined).map((s) => ({
        branchId: s.branchId, itemId: s.itemId, batchId: s.batchId!, batchNo: s.batchNo!,
        expiryDate: s.expiryDate!, name: s.nameEn, onHand: s.onHand,
        daysRemaining: Math.round((new Date(s.expiryDate!).getTime() - Date.now()) / 86_400_000),
        quarantined: true,
      })),
    }),

  postMovement: (_idempotencyKey, body) => {
    const line = STOCK.find((s) => s.itemId === body.itemId && s.branchId === body.branchId);
    if (line) line.onHand += body.quantity;
    return after({
      movementId: `mv000000-0000-4000-8000-${String(Date.now()).slice(-12)}`,
      replayed: false, quantity: body.quantity, onHand: line?.onHand ?? body.quantity,
    });
  },

  transfer: (_idempotencyKey, _body) =>
    // Two linked movements, so nothing is created or destroyed in transit — hence a net change of zero.
    after({
      transferRef: `TRF-${String(Date.now()).slice(-6)}`,
      outMovementId: "mv000000-0000-4000-8000-000000000101",
      inMovementId: "mv000000-0000-4000-8000-000000000102",
      netChange: 0,
    }),
};

export function createDevBranchApis(): BranchApis {
  return {
    branch: devBranchApi,
    roster: devRosterApi,
    availability: devAvailabilityApi,
    inventory: devInventoryApi,
  };
}
