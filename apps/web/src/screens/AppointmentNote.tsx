import { useState } from "react";
import { Button, Icon, Modal } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { useLoc } from "./_shared";

const S = {
  open: { en: "Appointment note", ar: "ملاحظة الموعد" },
  title: { en: "Appointment note", ar: "ملاحظة الموعد" },
  scope: {
    en: "A general note recorded at booking — access needs and arrangements. Not a clinical record.",
    ar: "ملاحظة عامة سُجلت عند الحجز — احتياجات الوصول والترتيبات. ليست سجلاً طبياً.",
  },
  close: { en: "Close", ar: "إغلاق" },
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
export function AppointmentNoteButton({ note }: { note?: string | null }) {
  const t = useLoc();
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
        // States the boundary where the note is actually read. The doctor opening this must not take it for
        // a clinical note — it was written at a desk by someone with no clinical authority.
        description={t(S.scope)}
        footer={<Button variant="secondary" onClick={() => setOpen(false)}>{t(S.close)}</Button>}
      >
        {/* pre-wrap: the operator's line breaks are part of what they wrote ("1. wheelchair 2. interpreter"),
            and collapsing them turns a list into a paragraph. */}
        <p style={{ margin: 0, whiteSpace: "pre-wrap" }}>{note}</p>
      </Modal>
    </>
  );
}
