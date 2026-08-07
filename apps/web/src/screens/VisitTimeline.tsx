import { useEffect, useState, type ReactNode } from "react";
import { Button, Icon, Modal, StatusChip } from "@mersal/design-system";
import type { AppointmentRow, Localized, TimelineStep } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useFormat } from "../i18n/useFormat";
import { useLoc, readErrorMessage } from "./_shared";

const S = {
  button: { en: "Timeline", ar: "المسار الزمني" },
  title: { en: "Appointment timeline", ar: "المسار الزمني للموعد" },
  subtitle: {
    en: "Everything recorded for this appointment, from booking onwards.",
    ar: "كل ما سُجّل لهذا الموعد، من الحجز فصاعداً.",
  },
  loading: { en: "Loading the timeline…", ar: "جاري تحميل المسار الزمني…" },
  empty: { en: "No recorded history for this appointment yet.", ar: "لا يوجد سجل لهذا الموعد بعد." },
  // The VISIT's own episode — everything done during this consultation.
  encTitle: { en: "Visit timeline", ar: "المسار الزمني للزيارة" },
  encSubtitle: {
    en: "Everything recorded during this visit, from the moment it started.",
    ar: "كل ما سُجّل خلال هذه الزيارة، منذ بدايتها.",
  },
  encEmpty: { en: "Nothing has been recorded in this visit yet.", ar: "لم يُسجَّل شيء في هذه الزيارة بعد." },
  // ONE transaction inside that episode — an order or a prescription.
  txTitle: { en: "Timeline", ar: "المسار الزمني" },
  txSubtitle: { en: "Everything recorded against this transaction", ar: "كل ما سُجّل على هذه المعاملة" },
  txEmpty: {
    en: "Nothing has been recorded against this one yet.",
    ar: "لم يُسجَّل شيء على هذه المعاملة بعد.",
  },
  /**
   * "by" survives as SCREEN-READER text only — the person glyph replaced it on screen.
   *
   * Without it the step announces "Reception" straight after a timestamp with nothing to say what the name
   * is doing there. The same for `at`: two bare values in a row are two facts a sighted reader separates by
   * their icons and a listening one cannot separate at all.
   */
  by: { en: "by", ar: "بواسطة" },
  at: { en: "at", ar: "في" },
  userRef: { en: "user reference", ar: "معرّف المستخدم" },
  /**
   * An actor the directory cannot name.
   *
   * It used to print eight hex characters of the subject id. That is not a name and not usable: nobody at a
   * desk can act on `0cccc773`, and it reads as a glitch rather than as information. The truth — we have a
   * record of who, and cannot resolve it to a person — is what is said, with the full id kept on `title` for
   * whoever is holding a support ticket. Still NEVER guessed at: an approximate actor is worse than an
   * honest absence, because the timeline exists to answer "who".
   */
  unknownActor: { en: "Unknown user", ar: "مستخدم غير معروف" },
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
  // 14.5 — EDITS, not states. The timeline used to collapse purely on status, so a reschedule left no trace
  // and the desk could not answer "why is this at 3pm when I was told 11?". These two are pseudo-statuses
  // emitted by AppointmentTimeline; they describe an act rather than a state the row was ever in, which is
  // why they read as verbs.
  Rescheduled: { kind: "info", label: { en: "Moved to a new time", ar: "نُقل إلى وقت جديد" } },
  NoteEdited: { kind: "neu", label: { en: "Note edited", ar: "تم تعديل الملاحظة" } },
  // A move to another practitioner at the same time. Its own step rather than folded into Rescheduled: a
  // patient told "same time, different doctor" is being told something a reschedule does not say, and the
  // desk fielding the call back needs to see which of the two happened.
  DoctorChanged: { kind: "info", label: { en: "Doctor changed", ar: "تغيّر الطبيب" } },

  // ---- Care-episode steps (ADR-0031) ----------------------------------------------------------------
  // An appointment is the START of an episode, and almost everything the platform then does for that
  // patient descends from it. Until these existed the timeline stopped at check-in, so a desk asking "why
  // is this member still here at four o'clock?" was shown a history that ended two hours before the
  // question. Each one names an ACT, never its content — see ADR-0031 §3.
  VisitStarted: { kind: "info", label: { en: "Visit started", ar: "بدأت الزيارة" } },
  VitalsRecorded: { kind: "neu", label: { en: "Vitals recorded", ar: "سُجّلت العلامات الحيوية" } },
  DiagnosisCoded: { kind: "neu", label: { en: "Diagnosis coded", ar: "سُجّل التشخيص" } },
  NoteSigned: { kind: "neu", label: { en: "Note signed", ar: "وُقّعت الملاحظة" } },
  VisitEnded: { kind: "ok", label: { en: "Visit ended", ar: "انتهت الزيارة" } },
  OrderPlaced: { kind: "info", label: { en: "Investigation ordered", ar: "طُلب فحص" } },
  OrderSentForApproval: { kind: "warn", label: { en: "Sent for approval", ar: "أُرسل للموافقة" } },
  OrderCancelled: { kind: "neu", label: { en: "Order cancelled", ar: "أُلغي الطلب" } },
  SampleConsumed: { kind: "info", label: { en: "Sample taken", ar: "أُخذت العينة" } },
  ResultReported: { kind: "ok", label: { en: "Result reported", ar: "صدرت النتيجة" } },
  AuthorizationDecided: { kind: "ok", label: { en: "Authorization decided", ar: "صدر قرار الموافقة" } },
  PrescriptionWritten: { kind: "info", label: { en: "Prescription written", ar: "كُتبت وصفة" } },
  // Same wording and the same `warn` cue as OrderSentForApproval, because they are the same fact about two
  // different things: this is waiting on a reviewer, not on the pharmacy.
  PrescriptionSentForApproval: { kind: "warn", label: { en: "Sent for approval", ar: "أُرسلت للموافقة" } },
  // The medication counterpart of OrderCancelled. Without it a cancelled prescription leaves no trace and
  // the episode still reads as though the medicine is waiting to be collected.
  PrescriptionCancelled: { kind: "neu", label: { en: "Prescription cancelled", ar: "أُلغيت الوصفة" } },
  MedicineDispensed: { kind: "ok", label: { en: "Medicine dispensed", ar: "صُرف الدواء" } },
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
  const t = useLoc();
  const [open, setOpen] = useState(false);
  const timeline = useTimeline(open, (a) => a.appointmentTimeline(row.id), [row.id]);

  return (
    <RowSafeTimeline>
      <Button variant="secondary" size="sm" leadingIcon={<Icon name="clock" />} onClick={() => setOpen(true)}>
        {t(S.button)}
      </Button>
      <Modal open={open} onOpenChange={setOpen} title={t(S.title)} description={t(S.subtitle)}
             footer={<Button variant="secondary" onClick={() => setOpen(false)}>{t(S.close)}</Button>}>
        <TimelineBody {...timeline} empty={S.empty} />
      </Modal>
    </RowSafeTimeline>
  );
}

/**
 * Keep this button AND ITS DIALOG from setting off the table row they live in.
 *
 * ============================================================================================================
 * A REACT PORTAL DOES NOT ESCAPE THE REACT TREE
 * ============================================================================================================
 * These timelines render inside a table CELL, on boards whose rows are themselves clickable. Stopping the
 * click on the trigger was not enough, and the reason is a genuine trap: `Modal` renders through
 * `Dialog.Portal`, so the dialog is a child of `document.body` in the DOM — but React dispatches its
 * synthetic events along the REACT tree, and in that tree the dialog is still a descendant of this cell.
 *
 * So every click inside the open dialog — the footer Close, the × in the corner, the scrim — bubbled to the
 * row's `onClick`. Dismissing the timeline therefore opened the row's own detail dialog behind it, which
 * looked like the timeline "turning into" the order. Escape was clean, because a key is not a click, and
 * that asymmetry is the tell.
 *
 * One boundary around the pair rather than a handler on each control: the dialog's chrome is Radix's, not
 * ours, so there is no complete list of clickable things inside it to remember to patch. Radix's own
 * handlers are further down the React tree and have already run by the time this sees the event, so
 * dismissal, focus return and the scrim all behave exactly as before.
 */
function RowSafeTimeline({ children }: { children: ReactNode }) {
  // `display: contents` — a wrapper for events, never for layout. Without it this span becomes an inline box
  // between the cell and its button and quietly changes the alignment of every row it is in.
  return (
    <span style={{ display: "contents" }} onClick={(e) => e.stopPropagation()}>
      {children}
    </span>
  );
}

/**
 * Load a timeline only once its dialog is OPEN, and drop the result if it closes first.
 *
 * A day board is dozens of rows and nobody wants the history of all of them — and each read is an audited PHI
 * access, so fetching eagerly would enter every patient's audit trail for timelines nobody opened.
 */
export function useTimeline(
  open: boolean,
  read: (api: ReturnType<typeof useApi>) => Promise<TimelineStep[]>,
  deps: unknown[],
): { steps: TimelineStep[] | null; error: Localized | null } {
  const api = useApi();
  const [steps, setSteps] = useState<TimelineStep[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    if (!open) return;
    let live = true;
    setSteps(null);
    setError(null);
    void read(api)
      .then((s) => live && setSteps(s))
      .catch((e) => {
        // A 403 here means the gate refused the row — say WHICH failure it was, not "no history". "No
        // recorded history" against a record you are simply not allowed to read is a lie about the record.
        if (live) setError(readErrorMessage(e));
      });
    return () => {
      live = false;
    };
    // `read` is an inline closure at every call site, so its identity is not a useful dependency; the caller
    // states what actually changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, open, ...deps]);

  return { steps, error };
}

/**
 * The steps themselves — loading, empty, error and the list.
 *
 * Shared by all three timelines (appointment, visit, and one transaction inside a visit) so they render as
 * one component. They answer different questions and there is no version of this where they should LOOK
 * different: the same acts, the same actors, the same clock.
 */
export function TimelineBody({
  steps,
  error,
  empty,
}: {
  steps: TimelineStep[] | null;
  error: Localized | null;
  empty: Localized;
}) {
  const t = useLoc();
  const fmt = useFormat();
  return (
        <div aria-live="polite">
          {error && <p role="alert">{t(error)}</p>}
          {!error && steps === null && <p role="status">{t(S.loading)}</p>}
          {!error && steps !== null && steps.length === 0 && <p role="status">{t(empty)}</p>}
          {!error && steps !== null && steps.length > 0 && (
            <ol className="vt-list">
              {steps.map((s, i) => (
                /*
                 * Two lines, not four columns.
                 *
                 * The row used to be `chip | time | actor` on fixed 132px/168px tracks, with the reference —
                 * added when the episode arrived — having no track at all, so it took the actor's and pushed
                 * them out of the row. And the step labels grew: "Prescription cancelled" does not fit 132px,
                 * so the chip overflowed its own pill and printed across the date beside it. Fixed tracks were
                 * always going to lose that race — Arabic sets these labels at different widths again.
                 *
                 * So the ACT and WHEN are the primary line, and the reference and actor are metadata under
                 * them. Only two columns have to agree now, the time is end-aligned so every date lines up
                 * regardless of the chip's width, and nothing has a hardcoded size.
                 */
                <li key={`${s.status}-${s.at}-${i}`} className="vt-step">
                  <StatusChip
                    kind={STATUS_CHIP[s.status]?.kind ?? "neu"}
                    label={t(STATUS_CHIP[s.status]?.label ?? { en: s.status, ar: s.status })}
                  />
                  <span className="vt-when tnum">
                    <Icon name="clock" width={13} height={13} aria-hidden="true" className="vt-ico" />
                    <span className="sr-only">{t(S.at)} </span>
                    {fmt.dateTime(s.at)}
                  </span>
                  {/*
                    The metadata line: WHO on the leading edge, directly under the act they performed, and the
                    business key — when there is one — pushed to the trailing edge of the same line.

                    The two were the other way round, so the actor's position moved depending on whether the
                    step happened to carry a reference: "Dr Karim" sat at the start of one row and halfway
                    across the next. The actor is the thing every step has and the thing a reader scans for,
                    so it gets the fixed edge.
                  */}
                  <span className="vt-meta">
                    <span className="vt-who">
                      {s.by || s.byName ? (
                        <>
                          <Icon name="user" width={13} height={13} aria-hidden="true" className="vt-ico" />
                          <span className="sr-only">{t(S.by)} </span>
                          {s.byName ? (
                            // A resolved name renders as a name.
                            <span className="vt-name">{s.byName}</span>
                          ) : (
                            // Unresolved — a deactivated account, or an actor from another tenant. Said in
                            // WORDS with the raw value on `title`: eight hex characters are not a name and
                            // nobody at a desk can act on them, so printing them put a glitch where an answer
                            // belongs. Never guessed at, which is what separates it from "not recorded".
                            <span className="muted" title={`${t(S.userRef)}: ${s.by}`}>{t(S.unknownActor)}</span>
                          )}
                        </>
                      ) : (
                        <span className="muted">{t(S.unattributed)}</span>
                      )}
                    </span>
                    {/* The business key the step is about — ENC-*, ORD-*, RX-*. It is the door to the thing,
                        not the thing: what it resolves to stays behind the owning service's own gate. */}
                    {s.reference && <code className="vt-ref tnum">{s.reference}</code>}
                  </span>
                </li>
              ))}
            </ol>
          )}
        </div>
  );
}

/**
 * The visit's own care episode — every act performed during this consultation (ADR-0031).
 *
 * <b>Why the workspace needs its own.</b> The appointment timeline reaches these same steps the long way
 * round, from the booking down to the encounter it produced. A doctor inside the workspace has no appointment
 * id in hand, and a WALK-IN has no appointment at all — so the visit's history was unreachable from the
 * screen documenting the visit.
 *
 * <b>`reference` narrows it to one transaction.</b> Every step carries the business key it belongs to
 * (ENC-*, ORD-*, RX-*), so the order and prescription dialogs pass their own reference and show only their
 * own history out of the same one read. That is why they cost no extra request.
 */
export function EncounterTimelineButton({
  encounterId,
  reference,
  label,
  context,
  variant = "secondary",
}: {
  encounterId: string;
  /** Show only the steps carrying this business key. Omit for the whole visit. */
  reference?: string;
  label?: Localized;
  /**
   * WHICH visit this is, when the button is opened from somewhere that does not already say.
   *
   * The encounter workspace needs none — you are standing in the visit. A row on "My Patients" is one
   * PERSON with several visits behind it, so a dialog headed "Visit timeline" there is ambiguous about the
   * one thing the reader has to know. Named rather than filtered: passing the encounter as `reference` would
   * also strip every step belonging to an order or a prescription, which is most of the visit.
   */
  context?: string;
  variant?: "secondary" | "ghost";
}) {
  const t = useLoc();
  const [open, setOpen] = useState(false);
  const { steps, error } = useTimeline(open, (a) => a.encounterTimeline(encounterId), [encounterId]);

  const shown = reference && steps ? steps.filter((s) => s.reference === reference) : steps;

  return (
    // The boundary covers the trigger AND the dialog — see `RowSafeTimeline`. Guarding only the trigger left
    // every click inside the open dialog bubbling to the row.
    <RowSafeTimeline>
      <Button variant={variant} size="sm" leadingIcon={<Icon name="clock" />} onClick={() => setOpen(true)}>
        {t(label ?? S.button)}
      </Button>
      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t(reference ? S.txTitle : S.encTitle)}
        description={reference
          ? `${t(S.txSubtitle)} · ${reference}`
          : context ? `${t(S.encSubtitle)} · ${context}` : t(S.encSubtitle)}
        footer={<Button variant="secondary" onClick={() => setOpen(false)}>{t(S.close)}</Button>}
      >
        <TimelineBody steps={shown} error={error} empty={reference ? S.txEmpty : S.encEmpty} />
      </Modal>
    </RowSafeTimeline>
  );
}
