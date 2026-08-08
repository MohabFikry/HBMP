import { useState } from "react";
import { Button, Icon, Modal } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";

const S = {
  open: { en: "Appointment note", ar: "ملاحظة الموعد" },
  title: { en: "Appointment note", ar: "ملاحظة الموعد" },
  /**
   * RETIRED from the dialog by request, and kept here deliberately.
   *
   * This was the only place in the application that told the reader a booking note is NOT clinical — the
   * doctor who opens it is reading free text written at a desk by somebody with no clinical authority, and
   * nothing else on any screen says so. Removing it was an explicit product decision; the string stays so
   * restoring it is one line rather than an archaeology exercise, and so the next person to read this file
   * knows the boundary used to be stated and where.
   */
  scope: {
    en: "A general note recorded at booking — access needs and arrangements. Not a clinical record.",
    ar: "ملاحظة عامة سُجلت عند الحجز — احتياجات الوصول والترتيبات. ليست سجلاً طبياً.",
  },
  close: { en: "Close", ar: "إغلاق" },
  // Both labels survive the redesign as SCREEN-READER text. The glyphs replaced them visually, and a person
  // icon beside a name announces nothing — without these the dialog would read out "Reception, 06 Aug 2026"
  // with no indication of which is the author and which is the moment.
  writtenBy: { en: "Written by", ar: "كتبها" },
  writtenAt: { en: "Written at", ar: "وقت الكتابة" },
  unknownAuthor: { en: "unknown", ar: "غير معروف" },
} satisfies Record<string, Localized>;

/**
 * The note affordance on an appointment row: an icon that opens the note in a modal.
 *
 * <b>Why an icon and not the text inline.</b> A note is up to 500 characters of free text, and a board is a
 * dense scan of twenty rows. Inlining it would push the time and status — the two things the desk reads on
 * every row — off the visible width, to show something most rows do not have. The icon marks which rows
 * carry one; the modal is where it is read.
 *
 * <b>Nothing is rendered when there is no note.</b> Not a greyed-out icon: an affordance that opens onto an
 * empty dialog teaches the operator to stop trusting the icon, and then they stop clicking the ones that do
 * have something.
 */
export function AppointmentNoteButton({
  note, by, at,
}: {
  note?: string | null;
  /**
   * Who wrote it, IN WORDS — shown beneath, so the reader can date the instruction and know who to ask.
   *
   * Deliberately the display name and never the subject id. The dialog was passed `noteBy` and rendered
   * "Written by c18b985c-cc5f-42eb-8b79-e41b7b84f975": a uuid answers "who told us this?" with a string
   * nobody at a desk can act on, which is the same as not answering. Notes written before emr 0022 carry no
   * name, and those say "unknown" — falling back to the id would put the uuid straight back on screen.
   */
  by?: string | null;
  at?: string | null;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [open, setOpen] = useState(false);

  if (!note) return null;

  return (
    <>
      <Button
        variant="ghost"
        size="sm"
        // An icon-only control still needs a name; without one a screen-reader user hears "button".
        aria-label={t(S.open)}
        title={t(S.open)}
        leadingIcon={<Icon name="doc" />}
        onClick={() => setOpen(true)}
      />
      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t(S.title)}
        footer={<Button variant="secondary" onClick={() => setOpen(false)}>{t(S.close)}</Button>}
      >
        {/*
          Attribution FIRST, as a header row: who on the leading edge, when on the trailing one.

          It reads before the text now rather than after it, which is the order the question is actually asked
          — a doctor deciding whether to act on "the sister will interpret" wants to know how old it is and
          who to ring before they weigh the instruction, not afterwards.

          Logical properties throughout and `space-between` rather than a fixed left/right, so the pair swaps
          ends in Arabic without a second rule.
        */}
        {(by || at) && (
          <p
            className="muted"
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "space-between",
              flexWrap: "wrap",
              gap: "var(--sp3)",
              margin: "0 0 var(--sp4)",
              fontSize: "var(--fs-caption)",
            }}
          >
            <span style={{ display: "inline-flex", alignItems: "center", gap: 6, minInlineSize: 0 }}>
              {/* Decorative: the glyph marks WHICH fact this is at a glance, and the sr-only label carries the
                  same thing for a reader that cannot see it. Announcing "person" adds nothing. */}
              <Icon name="user" aria-hidden="true" width={14} height={14} style={{ flex: "none" }} />
              <span className="sr-only">{t(S.writtenBy)} </span>
              <strong>{by ?? t(S.unknownAuthor)}</strong>
            </span>
            {at && (
              <span style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
                <Icon name="clock" aria-hidden="true" width={14} height={14} style={{ flex: "none" }} />
                <span className="sr-only">{t(S.writtenAt)} </span>
                <span className="tnum">{fmt.dateTime(at)}</span>
              </span>
            )}
          </p>
        )}
        {/*
          The note itself, in quotation marks.

          The marks are what separates the text from its attribution now that the rule is gone — they say "the
          following is somebody's own words" at any length, which is what a bare paragraph under a header row
          could not. Emphasised so they read as a frame rather than as stray punctuation the author typed.

          Real characters, not a decorative pseudo-element: they are punctuation around a quotation, and
          `“ ”` are direction-neutral, so the bidi algorithm places them correctly in Arabic without a mirror
          rule. pre-wrap stays — the operator's line breaks are part of what they wrote ("1. wheelchair
          2. interpreter"), and collapsing them turns a list into a paragraph.
        */}
        <p style={{ margin: 0, whiteSpace: "pre-wrap" }}>
          <span style={{ fontWeight: 700 }}>&ldquo;</span>
          {note}
          <span style={{ fontWeight: 700 }}>&rdquo;</span>
        </p>
      </Modal>
    </>
  );
}
