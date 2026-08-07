import { useState } from "react";
import { Button, Icon, InlineAlert, Modal, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type { BulkTemplateView } from "../api/policyApi";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import { useLoc } from "./_shared";

/**
 * The two actions that answer "what does this file have to look like?" — download the template, and read
 * the column contract — rendered as ONE matched pair, with the contract itself in a modal.
 *
 * ============================================================================================================
 * WHY THE COLUMN TABLE IS A MODAL AND NOT A SECTION
 * ============================================================================================================
 * The column contract is REFERENCE material: an operator reads it once, on their first file, and never again.
 * Rendered inline it pushed the actual work — choose a file, dry-run, commit — below the fold on every visit,
 * so the screen's most-used control was the hardest to reach. A modal puts reference behind one click and
 * gives the pipeline the top of the page.
 *
 * ============================================================================================================
 * WHAT MAKES HIDING IT SAFE
 * ============================================================================================================
 * Hiding a validation contract is only safe if a validation FAILURE can bring it straight back. The trigger is
 * therefore permanently visible next to the template button, and the parent can force the modal open from a
 * failure alert by passing `open`/`onOpenChange` — so "unknown column: xyz" is one click from the list of
 * columns that ARE known. The `templateHint` alert stays OUTSIDE this component, inline on the page: it is one
 * sentence, and it is the single line that prevents a failed upload.
 *
 * ============================================================================================================
 * WHY THE DOWNLOAD IS A FETCH AND NOT AN <a href download>
 * ============================================================================================================
 * It used to be a bare anchor, which sends no Authorization header — the one request in the app that did not
 * (audit R3, dead-link #2). Behind the gateway that is a 401 the browser renders as a broken download with no
 * message. So the bytes are fetched WITH the bearer token and handed to the browser as a blob.
 *
 * ONE component for both callers (Register New → "Many from a file", and Bulk & Imports) because they drive
 * the same engine. Two copies of the column table is how the two screens come to describe different contracts.
 */

const S = {
  template: { en: "Download the template", ar: "تنزيل القالب" },
  columns: { en: "Expected columns", ar: "الأعمدة المتوقعة" },
  columnsDesc: {
    en: "The engine matches on these names exactly. An unknown or missing column fails the whole file.",
    ar: "يطابق النظام هذه الأسماء تمامًا. أي عمود غير معروف أو ناقص يُفشل الملف بأكمله.",
  },
  column: { en: "Column", ar: "العمود" },
  required: { en: "Required", ar: "مطلوب" },
  meaning: { en: "Meaning", ar: "المعنى" },
  isRequired: { en: "Required", ar: "مطلوب" },
  isOptional: { en: "Optional", ar: "اختياري" },
  close: { en: "Close", ar: "إغلاق" },
  pending: {
    en: "The column list is still loading. Try again in a moment.",
    ar: "قائمة الأعمدة قيد التحميل. أعد المحاولة بعد لحظات.",
  },
  downloadFailed: {
    en: "The template could not be downloaded. Check your connection and try again.",
    ar: "لم يتم تنزيل القالب. تحقّق من الاتصال وأعد المحاولة.",
  },
} satisfies Record<string, Localized>;

export interface BulkTemplateActionsProps {
  /** Job type / template key — the path segment of the template endpoint. */
  jobType: string;
  /** The loaded contract. `null` while loading or unavailable — the trigger stays visible and explains itself. */
  template: BulkTemplateView | null;
  /** Optional controlled open, so a parent can reopen the contract from a validation failure. */
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
}

export function BulkTemplateActions({ jobType, template, open, onOpenChange }: BulkTemplateActionsProps) {
  const t = useLoc();
  const { lang } = useTheme();
  const [selfOpen, setSelfOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  const isOpen = open ?? selfOpen;
  const setOpen = (v: boolean) => {
    setSelfOpen(v);
    onOpenChange?.(v);
  };

  async function download() {
    setError(null);
    setBusy(true);
    try {
      const token = getToken();
      const res = await fetch(`${API_BASE}/bulk-templates/${jobType}`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      if (!res.ok) throw new Error(String(res.status));
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `${jobType}-template.csv`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch {
      setError(S.downloadFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="bulk-actions">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {/* Two actions of equal weight on the same question — matched treatment, never one button and one link. */}
      <div className="bulk-actions-row">
        <Button
          variant="secondary"
          leadingIcon={<Icon name="download" />}
          onClick={download}
          loading={busy}
          disabled={busy}
        >
          {t(S.template)}
        </Button>
        <Button
          variant="secondary"
          leadingIcon={<Icon name="doc" />}
          onClick={() => setOpen(true)}
          aria-haspopup="dialog"
          data-testid="expected-columns-trigger"
        >
          {t(S.columns)}
        </Button>
      </div>

      <Modal
        open={isOpen}
        onOpenChange={setOpen}
        title={t(S.columns)}
        description={t(S.columnsDesc)}
        closeLabel={t(S.close)}
        /* Three columns of exact match keys, read by scanning downward. At the default 520px the key column
           broke `card_number` across two lines (0B §10c). */
        wide
      >
        {template ? (
          <div className="bulk-columns mrs-scroll mrs-scroll-focusable" tabIndex={0} data-testid="expected-columns-table">
            <table className="pol-costshare">
              <caption className="sr-only">{t(S.columns)}</caption>
              <thead>
                <tr>
                  <th scope="col">{t(S.column)}</th>
                  <th scope="col">{t(S.required)}</th>
                  <th scope="col">{t(S.meaning)}</th>
                </tr>
              </thead>
              <tbody>
                {template.columns.map((c) => (
                  <tr key={c.name} id={`col-${c.name}`}>
                    <th scope="row"><code>{c.name}</code></th>
                    {/* Icon AND word — required-ness is never carried by a glyph alone (0B four-cue rule). */}
                    <td>
                      <span className="bulk-req">
                        <Icon name={c.required ? "ok" : "cross"} />
                        {t(c.required ? S.isRequired : S.isOptional)}
                      </span>
                    </td>
                    <td>{lang === "ar" ? c.descriptionAr : c.descriptionEn}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <InlineAlert tone="warn">{t(S.pending)}</InlineAlert>
        )}
      </Modal>
    </div>
  );
}
