import { z } from "zod";
import { zCoded, zDate, zId, zInstant, zLocalized, zStatus } from "./common";

/**
 * Doctor consultation / EMR (Phase 4). Every read here is treating-relationship gated on the server; the UI
 * only ever lists patients the doctor is currently treating. This is the ONE zone that legitimately holds
 * diagnosis + clinical narrative — the others (reception/lab/pharmacy/finance) structurally cannot.
 */
export const zPatientListItem = z.object({
  /** The ENCOUNTER id — this list is a worklist of encounters, not of people. */
  id: zId,
  /**
   * The beneficiary this encounter belongs to. Carried because the unified patient profile (design 39) is
   * keyed on the BENEFICIARY, and a worklist row that cannot name one is a row you cannot open a file from
   * — which is why the profile was unreachable from every clinical worklist.
   */
  beneficiaryId: zId,
  name: zLocalized,
  mrn: z.string(),
  /** True only when a treating relationship is active (server-asserted). */
  treating: z.boolean(),
  lastVisit: zDate.nullable(),
  status: zStatus,
  /**
   * Where this encounter happened — the branch of the appointment it was started from.
   *
   * An encounter has no branch of its own: it records CARE, and the place comes from the booking. So a
   * walk-in that was never booked has neither id nor name, and that null is a fact about the visit rather
   * than a gap in the response.
   *
   * The name is resolved client-side through the label-only `/branch-labels` lookup, because branch names
   * live behind `provider:read` and a doctor does not hold it. It stays null when that lookup fails —
   * an unnamed branch is better than no worklist.
   */
  branchId: zId.nullable(),
  branchName: z.string().nullable(),
});
export type PatientListItem = z.infer<typeof zPatientListItem>;

export const zVitals = z.object({
  heightCm: z.number().nullable(),
  weightKg: z.number().nullable(),
  systolic: z.number().nullable(),
  diastolic: z.number().nullable(),
  heartRate: z.number().nullable(),
  tempC: z.number().nullable(),
  /** Oxygen saturation, %. emr has stored SpO2 since phase 4; nothing had ever read it back. */
  spo2: z.number().nullable(),
  /** When this set was measured. A vitals panel with no time on it invites a doctor to read yesterday's
   *  observations as today's — the one reading error the panel itself can cause. */
  measuredAt: zInstant.nullable(),
});
export type Vitals = z.infer<typeof zVitals>;

export const zAllergy = z.object({ id: zId, substance: zLocalized, severity: z.enum(["mild", "moderate", "severe"]) });
export type Allergy = z.infer<typeof zAllergy>;

// ---------------------------------------------------------------- recording allergies + blood group
//
// The beneficiary-level clinical record, as the encounter workspace writes it. Distinct from `zAllergy`
// above, which is the read-only copy embedded in an encounter: recording one needs the masterdata allergen
// id, the status and the reaction, none of which a display chip carries.

/** ABO + Rh. Eight values, closed set — mirrors the CHECK constraint in emr migration 0021. */
export const zBloodGroup = z.enum(["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"]);
export type BloodGroup = z.infer<typeof zBloodGroup>;

export const zAllergySeverity = z.enum(["Mild", "Moderate", "Severe"]);
export type AllergySeverity = z.infer<typeof zAllergySeverity>;

export const zAllergyStatus = z.enum(["Active", "Inactive", "Resolved"]);
export type AllergyStatus = z.infer<typeof zAllergyStatus>;

/** A row of the masterdata allergen catalogue — what the picker offers. */
export const zAllergenOption = z.object({
  allergenId: zId,
  code: z.string(),
  name: z.string(),
  /** Drug / Food / Environmental. Drug-class allergens are the ones prescribe-time screening resolves. */
  category: z.enum(["Drug", "Food", "Environmental"]),
});
export type AllergenOption = z.infer<typeof zAllergenOption>;

/**
 * One allergy as recorded in the member's file.
 *
 * `allergen` is the substance's NAME, snapshot by emr at the moment of recording (its migration 0020). It is
 * nullish for rows written before that migration — the UI says "(unspecified)" rather than showing the uuid,
 * because a safety strip that displays an identifier has stopped communicating.
 */
export const zAllergyRecord = z.object({
  allergyId: zId,
  allergenId: zId,
  allergen: z.string().nullish(),
  reaction: z.string().nullish(),
  severity: zAllergySeverity,
  status: zAllergyStatus,
});
export type AllergyRecord = z.infer<typeof zAllergyRecord>;

/**
 * The standing clinical facts a clinician needs before acting: blood group and the allergy list.
 *
 * <b>`allergies: []` does not mean "no allergies".</b> It means nobody has recorded any, and the two are
 * different clinical claims — the same distinction the prescribing workspace draws between `Ok` and
 * `NotChecked`, and the reason its allergy check reports NotChecked at zero recorded allergens rather than
 * reporting a clean screen. Callers must render absence as absence.
 */
export const zMemberClinicalRecord = z.object({
  beneficiaryId: zId,
  bloodGroup: zBloodGroup.nullish(),
  bloodGroupRecordedAt: zInstant.nullish(),
  allergies: z.array(zAllergyRecord),
});
export type MemberClinicalRecord = z.infer<typeof zMemberClinicalRecord>;

/** Record a new allergy against the member's file. */
export const zAddAllergyRequest = z.object({
  allergenId: zId,
  reaction: z.string().max(120).nullish(),
  severity: zAllergySeverity,
  status: zAllergyStatus,
});
export type AddAllergyRequest = z.infer<typeof zAddAllergyRequest>;

export const zSoap = z.object({
  subjective: z.string(),
  objective: z.string(),
  assessment: z.string(),
  plan: z.string(),
});
export type Soap = z.infer<typeof zSoap>;

/** Which of an encounter's coded conditions the visit was chiefly about. */
export const zDiagnosisRank = z.enum(["Primary", "Secondary"]);
export type DiagnosisRank = z.infer<typeof zDiagnosisRank>;

/**
 * A diagnosis AS RECORDED ON THIS ENCOUNTER — a coded condition plus the id of the row carrying it.
 *
 * `zCoded` alone was enough while the workspace only displayed diagnoses. It is not enough to retract one:
 * the same ICD code can legitimately be recorded twice on one encounter (different rank, different clinical
 * status), so the code does not identify the row, and a retract keyed on it would remove whichever one the
 * server happened to find first.
 */
export const zEncounterDiagnosis = zCoded.extend({
  /** Null for a code the doctor has just added and not yet saved — there is no row to address yet. */
  id: zId.nullable(),
  /**
   * Primary or secondary. An encounter has ONE primary diagnosis — the condition it was chiefly about, and
   * the one the authorization, the claim and the formulary check all key on. Everything else recorded on the
   * visit is secondary. Carried on the row rather than inferred from position, because the order emr returns
   * diagnoses in is insertion order and says nothing about which one mattered.
   */
  rank: zDiagnosisRank,
});
export type EncounterDiagnosis = z.infer<typeof zEncounterDiagnosis>;

export const zEncounter = z.object({
  id: zId,
  patientId: zId,
  patientName: zLocalized,
  openedAt: zInstant,
  signed: z.boolean(),
  /**
   * The working SOAP note this encounter is documented in, or null when nothing has been written yet.
   *
   * The workspace needs it to know which verb to use: a first save CREATES the note, every save after that
   * UPDATES it, and signing addresses it by id. Without it the editor could only ever create, and each save
   * would leave another partial note behind on the encounter.
   */
  noteId: zId.nullable(),
  soap: zSoap,
  vitals: zVitals,
  allergies: z.array(zAllergy),
  diagnoses: z.array(zEncounterDiagnosis),
});
export type Encounter = z.infer<typeof zEncounter>;

/** An ICD-10 code as master data returns it from a search — the picker's row, and nothing more. */
export const zIcdRef = z.object({ code: z.string().min(1), title: z.string() });
export type IcdRef = z.infer<typeof zIcdRef>;

/** Place an investigation order (routed to approval when high-cost — server decides). */
export const zPlaceOrderRequest = z.object({
  encounterId: zId,
  kind: z.enum(["lab", "radiology"]),
  test: zCoded,
  priority: z.enum(["routine", "urgent"]),
  notes: z.string().max(500).optional(),
});
export type PlaceOrderRequest = z.infer<typeof zPlaceOrderRequest>;

export const zPlaceOrderResult = z.object({
  orderId: zId,
  status: zStatus,
  /** True when the order tripped high-cost routing and now awaits medical approval. */
  requiresApproval: z.boolean(),
});
export type PlaceOrderResult = z.infer<typeof zPlaceOrderResult>;

/** Write an e-prescription line (interaction/allergy checks are advisory + server-side). */
export const zPrescribeRequest = z.object({
  encounterId: zId,
  drug: zCoded,
  dose: z.string().min(1),
  quantity: z.number().int().positive(),
  notes: z.string().max(500).optional(),
});
export type PrescribeRequest = z.infer<typeof zPrescribeRequest>;

export const zPrescribeResult = z.object({
  prescriptionId: zId,
  status: zStatus,
  /** Advisory alerts (interactions/allergies) — non-blocking; the doctor acknowledges. */
  advisories: z.array(zLocalized),
});
export type PrescribeResult = z.infer<typeof zPrescribeResult>;
