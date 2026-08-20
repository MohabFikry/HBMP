import { useRef, useState } from "react";
import { Button, Icon, InlineAlert } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { getToken } from "../auth/tokenStore";
import { GATEWAY_BASE } from "../config";
import { StaffAvatar, invalidateStaffPhoto, useStaffPhoto } from "./StaffAvatar";

/**
 * Choose a photograph — 28.15. Used by a person on their own account, and by an administrator on somebody's.
 *
 * ============================================================================================================
 * THE BROWSER DOES THE RESIZING, AND THAT IS THE POINT
 * ============================================================================================================
 * A phone camera produces four megabytes; the server's cap is 512 KB. Uploading the original and letting the
 * server refuse it would mean everybody who tries from a phone is told "too large" with no way to comply —
 * they have no image editor to hand. So the file is drawn onto a canvas, cropped square from the centre and
 * re-encoded at 512px before it leaves. A four-megabyte photo becomes roughly forty kilobytes and the person
 * never learns there was a limit.
 *
 * It is a convenience, NOT a control. The server sniffs the magic bytes, bounds the read and enforces the cap
 * regardless of what this sends — everything here runs on the uploader's machine and can be bypassed by
 * anyone who cares to.
 */

const S = {
  photo: { en: "Photo", ar: "الصورة" },
  choose: { en: "Choose a photo", ar: "اختر صورة" },
  replace: { en: "Replace", ar: "استبدال" },
  remove: { en: "Remove", ar: "إزالة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  // The prose that used to sit under the buttons is gone. It explained the resizing (which nobody needs to
  // know, because it happens), and repeated "it grants nothing" (which the Position field above it already
  // says). What is left is the control.
  add: { en: "Add a photo", ar: "إضافة صورة" },
  change: { en: "Change photo", ar: "تغيير الصورة" },
  photoTitle: { en: "Photo", ar: "الصورة" },
  notAnImage: { en: "That file is not an image.", ar: "هذا الملف ليس صورة." },
  failed: { en: "The photo could not be saved. Try again.", ar: "تعذّر حفظ الصورة. حاول مرة أخرى." },
} satisfies Record<string, Localized>;

/** The edge of the square we upload. 512 is sharp on a retina 56px avatar and still tens of kilobytes. */
const EDGE = 512;

/**
 * Draw the chosen file square and re-encode it.
 *
 * <p>Cropped from the CENTRE rather than squashed: a portrait photo scaled to a square makes the face wider
 * than it is, which on a page full of colleagues is more noticeable than a cropped shoulder.</p>
 */
async function toSquarePng(file: File): Promise<Blob | null> {
  const bitmap = await createImageBitmap(file).catch(() => null);
  if (!bitmap) return null; // Not decodable as an image — the file dialog's accept filter is only a hint.

  const edge = Math.min(bitmap.width, bitmap.height);
  const sx = (bitmap.width - edge) / 2;
  const sy = (bitmap.height - edge) / 2;

  const canvas = document.createElement("canvas");
  canvas.width = EDGE;
  canvas.height = EDGE;
  const ctx = canvas.getContext("2d");
  if (!ctx) return null;
  ctx.drawImage(bitmap, sx, sy, edge, edge, 0, 0, EDGE, EDGE);
  bitmap.close();

  return await new Promise((resolve) => canvas.toBlob((b) => resolve(b), "image/png"));
}

export function PhotoPicker({
  userId,
  name,
  /** Absent for self-service; an account id when an administrator is setting somebody else's. */
  adminForUserId,
  /**
   * `hover` — the avatar IS the control: an icon appears over it, upload when there is no photo and edit
   * when there is, and editing reveals replace and remove beneath it.
   * `buttons` — an explicit pair beside the avatar, for a form where every other row is a labelled control
   * and a picture that only responds to hovering would be the one thing on the page hiding its verb.
   */
  variant = "hover",
  t,
}: {
  userId: string | undefined;
  name: string;
  adminForUserId?: string;
  variant?: "hover" | "buttons";
  t: (l: Localized) => string;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);
  const [editing, setEditing] = useState(false);

  const subject = adminForUserId ?? userId;
  // The SAME store every other avatar reads. Not a local copy: the app bar and this control showed two
  // different pictures of one person when each did its own fetch (28.15 follow-up).
  const src = useStaffPhoto(subject);

  const base = adminForUserId
    ? `${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(adminForUserId)}/photo`
    : `${GATEWAY_BASE}/identity/me/photo`;

  async function send(method: "PUT" | "DELETE", body?: Blob) {
    setBusy(true);
    setError(null);
    try {
      const token = getToken();
      const res = await fetch(base, {
        method,
        headers: {
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
          ...(body ? { "Content-Type": "image/png" } : {}),
        },
        body,
      });
      if (!res.ok) {
        setError(S.failed);
        return;
      }
      // One invalidation, every avatar. This is what makes the bar and the pane agree.
      if (subject) invalidateStaffPhoto(subject);
      setEditing(false);
    } catch {
      setError(S.failed);
    } finally {
      setBusy(false);
    }
  }

  async function onPick(file: File) {
    const square = await toSquarePng(file);
    if (!square) {
      setError(S.notAnImage);
      return;
    }
    await send("PUT", square);
  }

  const pick = () => inputRef.current?.click();

  const fileInput = (
    /*
      `sr-only` rather than `display: none`: a hidden input is still the accessible control here, and
      `display:none` removes it from the accessibility tree entirely. `accept` is a FILTER for the file
      dialog and nothing more — the server sniffs the bytes regardless of what it lets through.
    */
    <input
      ref={inputRef}
      type="file"
      className="sr-only"
      accept="image/png,image/jpeg,image/webp"
      aria-label={t(src ? S.change : S.add)}
      onChange={(e) => {
        const file = e.currentTarget.files?.[0];
        // Reset first: picking the SAME file twice fires no change event otherwise, so a failed upload
        // could not be retried without choosing a different image.
        e.currentTarget.value = "";
        if (file) void onPick(file);
      }}
    />
  );

  if (variant === "buttons") {
    return (
      <div className="photo-picker">
        <StaffAvatar userId={subject} name={name} size={64} />
        <div className="photo-picker-controls">
          <div className="chip-row">
            <Button variant="secondary" size="sm" leadingIcon={<Icon name="upload" />} loading={busy} onClick={pick}>
              {t(src ? S.replace : S.choose)}
            </Button>
            <Button variant="ghost" size="sm" disabled={busy || !src} onClick={() => void send("DELETE")}>
              {t(S.remove)}
            </Button>
          </div>
          {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
        </div>
        {fileInput}
      </div>
    );
  }

  return (
    <div className="photo-picker--hover">
      {/*
        The avatar IS the button. The icon over it appears on hover AND on keyboard focus — an affordance
        that only exists under a pointer is one a keyboard user never learns about, and this is the sole
        control here. The accessible name states the verb outright for the same reason.
      */}
      <button
        type="button"
        className="photo-drop"
        onClick={() => (src ? setEditing((v) => !v) : pick())}
        aria-label={t(src ? S.change : S.add)}
        aria-expanded={src ? editing : undefined}
        disabled={busy}
      >
        <StaffAvatar userId={subject} name={name} size={64} />
        <span className="photo-drop-overlay" aria-hidden="true">
          <Icon name={src ? "pen" : "upload"} />
        </span>
      </button>

      {/*
        AN INLINE DISCLOSURE, NOT A DIALOG — and that is a bug fix, not a preference.

        This was a `Modal`. Radix portals it to `document.body`, which is OUTSIDE the account pane's own
        panel — and that pane dismisses itself on any `mousedown` its panel does not stop. So pressing
        Replace or Remove closed the whole pane, unmounting the modal mid-click: both buttons appeared to do
        nothing. A dialog nested inside a hand-rolled drawer has two focus traps and two dismissal rules
        fighting each other; a disclosure inside the same panel has neither.
      */}
      {src && editing && (
        <div className="chip-row">
          <Button variant="secondary" size="sm" leadingIcon={<Icon name="upload" />} loading={busy} onClick={pick}>
            {t(S.replace)}
          </Button>
          <Button variant="danger" size="sm" loading={busy} onClick={() => void send("DELETE")}>
            {t(S.remove)}
          </Button>
        </div>
      )}

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {fileInput}
    </div>
  );
}

export const PHOTO_PICKER_STRINGS = S;
