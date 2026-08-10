import { useState } from "react";
import { Button, Icon, InlineAlert } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import { useLoc } from "./_shared";

/**
 * Download the row-error report a bulk job produced.
 *
 * ============================================================================================================
 * WHY THIS DID NOT EXIST
 * ============================================================================================================
 * Everything else was already built. The engine writes the full error list to document-service the moment a
 * job has any errors, because the list quotes member numbers and belongs behind an authorization rather than
 * in a JSON body. document-service serves it from `/operational-documents/{id}/content` through the
 * authorization engine, and audits every read as an Export with a `phi` field class — its own comment says
 * "the fourth download of it is exactly as much of a disclosure as the first". Kong routes it. The SPA got
 * back an `errorDocumentId` and rendered a sentence saying the report "is downloaded through an authorized,
 * audited request", and offered no way to make one.
 *
 * So the operator was told a complete report existed, shown fifty rows of it, and left to re-upload blind.
 *
 * ============================================================================================================
 * A FETCH, NOT AN <a href download>
 * ============================================================================================================
 * The same reason `BulkTemplateActions` gives: an anchor sends no Authorization header, and behind the
 * gateway that is a 401 the browser renders as a broken download with no message. The endpoint streams bytes
 * rather than handing out a signed URL — deliberately, so the file cannot outlive the authorization that
 * fetched it — which means `window.open` is not an option either. The bytes come back with the token
 * attached and go to the browser as a blob.
 */

const S = {
  download: { en: "Download the full error report", ar: "تنزيل تقرير الأخطاء الكامل" },
  failed: {
    en: "The error report could not be downloaded. Check your connection and try again.",
    ar: "تعذّر تنزيل تقرير الأخطاء. تحقّق من الاتصال وأعد المحاولة.",
  },
  denied: {
    en: "You are not permitted to download this report. It contains member data.",
    ar: "غير مصرّح لك بتنزيل هذا التقرير. يحتوي على بيانات أعضاء.",
  },
} satisfies Record<string, Localized>;

export interface BulkErrorReportButtonProps {
  /** The job's `errorDocumentId`. Nothing renders when there is no report — see the note below. */
  documentId: string | null | undefined;
  /** Names the file the browser saves. */
  jobId: string;
}

export function BulkErrorReportButton({ documentId, jobId }: BulkErrorReportButtonProps) {
  const t = useLoc();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  // No report, no button. A control that 404s is worse than an absent one — and the engine logs a warning
  // when storage failed, so an absent id is a real state rather than a rendering accident.
  if (!documentId) return null;

  async function download() {
    setError(null);
    setBusy(true);
    try {
      const token = getToken();
      const res = await fetch(`${API_BASE}/operational-documents/${documentId}/content`, {
        headers: token ? { Authorization: `Bearer ${token}` } : {},
      });
      // 403 is the authorization engine, not an outage, and it says something different to the operator:
      // retrying will not help, and the reason is that the file names people.
      if (res.status === 403) throw new Error("denied");
      if (!res.ok) throw new Error(String(res.status));

      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `errors-${jobId}.csv`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (e) {
      setError(e instanceof Error && e.message === "denied" ? S.denied : S.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div>
        <Button
          variant="secondary"
          size="sm"
          leadingIcon={<Icon name="download" />}
          loading={busy}
          onClick={() => void download()}
        >
          {t(S.download)}
        </Button>
      </div>
      <div aria-live="polite">{error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}</div>
    </>
  );
}
