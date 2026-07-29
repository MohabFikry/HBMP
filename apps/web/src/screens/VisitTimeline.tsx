import { useEffect, useState } from "react";
import { Button, Modal, StatusChip } from "@mersal/design-system";
import type { AppointmentRow, Localized, TimelineStep } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useFormat } from "../i18n/useFormat";
import { useLoc, readErrorMessage } from "./_shared";

const S = {
  button: { en: "Timeline", ar: "المسار الزمني" },
  title: { en: "Appointment timeline", ar: "المسار الزمني للموعد" },
  subtitle: {
    en: "How this appointment reached its current status.",
    ar: "كيف وصل هذا الموعد إلى حالته الحالية.",
  },
  loading: { en: "Loading the timeline…", ar: "جاري تحميل المسار الزمني…" },
  empty: { en: "No recorded history for this appointment yet.", ar: "لا يوجد سجل لهذا الموعد بعد." },
  by: { en: "by", ar: "بواسطة" },
  userRef: { en: "user reference", ar: "معرّف المستخدم" },
  unattributed: {
    en: "actor not recorded",
    ar: "لم يُسجَّل المنفّذ",
  },
  close: { en: "Close", ar: "إغلاق" },
} satisfies Record<string, Localized>;

/** The timeline carries raw emr enum names; the desk should read words, and the chip needs a non-colour kind
 *  alongside them (hue + icon + text — 0B / 21). Unknown statuses fall back to the literal rather than to a
 *  wrong colour, so a new enum value shows up plainly instead of silently reading as "fine". */
const STATUS_CHIP: Record<string, { kind: "ok" | "info" | "warn" | "neu"; label: Localized }> = {
  Booked: { kind: "info", label: { en: "Booked", ar: "محجوز" } },
  CheckedIn: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } },
  Completed: { kind: "neu", label: { en: "Completed", ar: "مكتمل" } },
  NoShow: { kind: "warn", label: { en: "No-show", ar: "لم يحضر" } },
  Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } },
};

/**
 * The timeline button on an appointment row (23 §1 — "all should be tracked").
 *
 * Loaded on OPEN, not with the board: a day board is dozens of rows and nobody wants the history of all of
 * them. Sourced from emr's own appointment history under appointment:read — deliberately not the audit store,
 * which is hash-chained, spans every entity, and requires audit:read (Security/Compliance/DPO). The desk needs
 * the status steps of one appointment, not the compliance record.
 */
export function VisitTimelineButton({ row }: { row: AppointmentRow }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const [open, setOpen] = useState(false);
  const [steps, setSteps] = useState<TimelineStep[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    if (!open) return;
    let live = true;
    setSteps(null);
    setError(null);
    void api
      .appointmentTimeline(row.id)
      .then((s) => live && setSteps(s))
      .catch((e) => {
        // A 403 here means branch scope refused the row — say which failure it was, not "no history".
        if (live) setError(readErrorMessage(e));
      });
    return () => {
      live = false;
    };
  }, [api, open, row.id]);

  return (
    <>
      <Button variant="secondary" size="sm" onClick={() => setOpen(true)}>
        {t(S.button)}
      </Button>
      <Modal open={open} onOpenChange={setOpen} title={t(S.title)} description={t(S.subtitle)}
             footer={<Button variant="secondary" onClick={() => setOpen(false)}>{t(S.close)}</Button>}>
        <div aria-live="polite">
          {error && <p role="alert">{t(error)}</p>}
          {!error && steps === null && <p role="status">{t(S.loading)}</p>}
          {!error && steps !== null && steps.length === 0 && <p role="status">{t(S.empty)}</p>}
          {!error && steps !== null && steps.length > 0 && (
            <ol className="vt-list">
              {steps.map((s, i) => (
                <li key={`${s.status}-${s.at}-${i}`} className="vt-step">
                  <StatusChip
                    kind={STATUS_CHIP[s.status]?.kind ?? "neu"}
                    label={t(STATUS_CHIP[s.status]?.label ?? { en: s.status, ar: s.status })}
                  />
                  <span className="vt-when tnum">{fmt.dateTime(s.at)}</span>
                  {/* An unrecorded actor says so. Falling back to whoever booked it would claim they performed
                      a step they did not — the timeline exists to answer "who", so guessing defeats it. */}
                  <span className="vt-who">
                    {s.byName ? (
                      // A resolved name renders as a name.
                      <>
                        {t(S.by)} <span className="vt-name">{s.byName}</span>
                      </>
                    ) : s.by ? (
                      // Unresolved — a deactivated account, or another tenant's actor. Truncated and monospaced
                      // with the full value in the title, so it reads as an identifier instead of being mistaken
                      // for a person's name. Never guessed at: an approximate actor is worse than a visible id.
                      <>
                        {t(S.by)}{" "}
                        <code className="vt-actor" title={`${t(S.userRef)}: ${s.by}`}>{s.by.slice(0, 8)}</code>
                      </>
                    ) : (
                      <span className="muted">{t(S.unattributed)}</span>
                    )}
                  </span>
                </li>
              ))}
            </ol>
          )}
        </div>
      </Modal>
    </>
  );
}
