import { useCallback, useEffect, useState } from "react";
import { Button, Select, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { useFormat } from "../i18n/useFormat";
import type { CcApi, CcClinic, CcSlot } from "./CallCentre";

/**
 * Branch → clinic → time, extracted so the call workspace and the standalone "Book appointment" screen share
 * ONE implementation. They previously would have held two copies of the same three-step dependency chain, and
 * the interesting part of that chain is what it INVALIDATES: changing the branch must drop the clinic chosen
 * under the old one and the times chosen under that clinic, in the same batch, or a render exists where the
 * agent is looking at Dokki times under a Nasr City heading. One copy, one place that rule can be wrong.
 */
export interface Reservation {
  clinics: CcClinic[];
  /** [branchId, branchName] for the branches that actually have availability. */
  branches: [string, string][];
  branchKey: string;
  clinicKey: string;
  branchClinics: CcClinic[];
  chosenClinic: CcClinic | null;
  slots: CcSlot[];
  slotId: string;
  pickBranch: (key: string) => void;
  pickClinic: (key: string) => void;
  pickSlot: (slotId: string) => void;
  /** Re-read the chosen clinic's times and drop the selection — used after a booking or a 409. */
  refresh: () => void;
}

export function useReservation(api: CcApi, enabled: boolean): Reservation {
  const [clinics, setClinics] = useState<CcClinic[]>([]);
  const [branchKey, setBranchKey] = useState("");
  const [clinicKey, setClinicKey] = useState("");
  const [slots, setSlots] = useState<CcSlot[]>([]);
  const [slotId, setSlotId] = useState("");

  /**
   * The branches the agent may book into, derived from the clinics that actually have availability — so a
   * branch is never offered that would then present no clinic. This is where the call centre's wider scope
   * belongs: at the moment of the decision, naming the branch the appointment is FOR. It is deliberately not
   * an app-bar filter; a global "all branches" chip states the scope, changes nothing, and invites the agent
   * to think it is narrowing what they see.
   */
  const branches = [...new Map(
    clinics.filter((c) => c.branchId).map((c) => [c.branchId!, c.branchName ?? c.branchId!] as [string, string]),
  ).entries()];
  const branchClinics = clinics.filter((c) => (c.branchId ?? "") === branchKey);
  const chosenClinic = branchClinics.find((c) => `${c.providerId}|${c.locationId}` === clinicKey) ?? null;

  useEffect(() => {
    if (!enabled) return;
    let live = true;
    void api.clinics().then((c) => live && setClinics(c)).catch(() => live && setClinics([]));
    return () => { live = false; };
  }, [api, enabled]);

  useEffect(() => {
    // A clinic is a provider+location pair chosen as ONE value, so there is no render where the pair is half
    // updated and the times belong to a clinic nobody picked.
    setSlots([]);
    setSlotId("");
    if (!chosenClinic) return;
    let live = true;
    void api.slots(chosenClinic.providerId, chosenClinic.locationId)
      .then((sl) => live && setSlots(sl))
      .catch(() => live && setSlots([]));
    return () => { live = false; };
  }, [api, chosenClinic?.providerId, chosenClinic?.locationId]);

  const pickBranch = useCallback((key: string) => {
    setBranchKey(key);
    setClinicKey("");
    setSlots([]);
    setSlotId("");
  }, []);

  const pickClinic = useCallback((key: string) => {
    setClinicKey(key);
    setSlots([]);
    setSlotId("");
  }, []);

  const refresh = useCallback(() => {
    setSlotId("");
    if (!chosenClinic) return;
    void api.slots(chosenClinic.providerId, chosenClinic.locationId).then(setSlots).catch(() => setSlots([]));
  }, [api, chosenClinic?.providerId, chosenClinic?.locationId]);

  return {
    clinics, branches, branchKey, clinicKey, branchClinics, chosenClinic, slots, slotId,
    pickBranch, pickClinic, pickSlot: setSlotId, refresh,
  };
}

/**
 * The picker itself. Arrivals are deliberately absent — no check-in, no no-show, no start-visit. The server
 * enforces that with `appointment:reserve` rather than `appointment:write`, so the missing buttons are
 * presentation, not the boundary.
 */
export function ReservationPicker({
  r, onBook, bookLabel, disabled = false,
}: {
  r: Reservation;
  onBook: () => void;
  bookLabel: string;
  disabled?: boolean;
}) {
  const fmt = useFormat();
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];

  // Grouped by DAY. Availability spans a week, so a flat wall of times repeats "09:40" once per day with
  // nothing to tell them apart — the agent cannot book a specific day, which is most of what a caller rings
  // to do.
  const byDay = r.slots.reduce<Record<string, CcSlot[]>>((acc, sl) => {
    const day = fmt.date(sl.start);
    (acc[day] ??= []).push(sl);
    return acc;
  }, {});

  return (
    <div className="cc-reserve">
      <p className="cc-muted">{t(L.ccReserveOnly)}</p>
      {r.clinics.length === 0 ? (
        <p role="status">{t(L.ccNoClinics)}</p>
      ) : (
        <>
          {/* The design-system Select, not a native one: a native <select> draws its option list in the OS, so
              it arrives system-blue with square corners no matter what CSS we write — the same reason the
              branch switcher was rebuilt onto this component. */}
          <div className="cc-field">
            <span id="cc-branch-label">{t(L.ccBranch)}</span>
            <Select
              aria-labelledby="cc-branch-label"
              value={r.branchKey || null}
              placeholder={t(L.ccPickBranch)}
              options={r.branches.map(([id, name]) => ({ value: id, label: name }))}
              onChange={r.pickBranch}
            />
          </div>
          <div className="cc-field">
            <span id="cc-clinic-label">{t(L.ccClinic)}</span>
            <Select
              aria-labelledby="cc-clinic-label"
              value={r.clinicKey || null}
              disabled={!r.branchKey}
              placeholder={r.branchKey ? t(L.ccPickClinic) : t(L.ccPickBranchFirst)}
              options={r.branchClinics.map((c) => ({
                value: `${c.providerId}|${c.locationId}`,
                label: `${c.label} · ${c.openSlots}`,
              }))}
              onChange={r.pickClinic}
            />
          </div>
          {r.chosenClinic && r.slots.length === 0 && <p role="status">{t(L.ccNoSlots)}</p>}
          {Object.entries(byDay).map(([day, daySlots]) => (
            <div key={day} className="cc-day">
              <h4 className="cc-day-label">{day}</h4>
              <div className="cc-slots" role="radiogroup" aria-label={`${t(L.ccTime)} — ${day}`}>
                {daySlots.map((sl) => (
                  <button
                    key={sl.slotId}
                    type="button"
                    role="radio"
                    aria-checked={r.slotId === sl.slotId}
                    // The accessible name carries the DAY too, so a screen-reader user is not offered eighty
                    // identical "09:40" buttons.
                    aria-label={`${day} ${fmt.time(sl.start)}`}
                    className="book-slot"
                    onClick={() => r.pickSlot(sl.slotId)}
                  >
                    <span className="tnum">{fmt.time(sl.start)}</span>
                  </button>
                ))}
              </div>
            </div>
          ))}
        </>
      )}
      {/* Disabled until a real slot is chosen: the id used to be invented client-side, so every reservation
          the call centre made referred to a slot that could not exist. */}
      <Button variant="primary" onClick={onBook} disabled={disabled || !r.slotId}>{bookLabel}</Button>
    </div>
  );
}
