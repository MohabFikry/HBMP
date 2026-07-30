import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, Card, Icon, InlineAlert, InputField, Modal, Select, StatusChip } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type { PolicyApi, PolicyDocumentView } from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";
import { writeErrorMessage } from "../api/writeError";
import { useLoc, readErrorMessage } from "./_shared";
import { useFormat } from "../i18n/useFormat";

/** ONE client for the module, not one per render — a fresh instance per render turns a load effect keyed on
 *  the api into an unbounded request loop (the QA P0-1 defect the policy screens were built around). */
const httpPolicyApi = createHttpPolicyApi();

/**
 * The beneficiary's documents: file them at the desk, see them in place, take a copy.
 *
 * ============================================================================================================
 * THE TYPE LIST IS A MAPPING, NOT A NEW VOCABULARY
 * ============================================================================================================
 * The six operational kinds Mersal files against a member all have a home in the `DocumentClass` the server
 * already enforces classification and role rules against. Inventing a parallel list here would produce labels
 * with nothing behind them: a "medical file" that carried no clinical floor would be readable by finance, and
 * a second list is a second place for the rules to be forgotten.
 *
 * Personal photo maps to `IdentityPhoto`, which is consent-gated on the server and is what the member file
 * renders as their avatar — so choosing it here IS the way a photo reaches the member's file.
 *
 * ============================================================================================================
 * LOOKING AND TAKING ARE DIFFERENT ACTS
 * ============================================================================================================
 * The eye and the download icon both resolve a short-TTL signed URL through the same audited endpoint, but
 * they send a different `purpose`, so a year later the record can distinguish an officer who glanced at a
 * card scan from one who took a copy of it. Neither is offered when the server says `canDownload` is false;
 * that row renders as a named locked state rather than a button that 403s.
 */

const S = {
  title: { en: "Documents", ar: "المستندات" },
  intro: {
    en: "File the paperwork for this member. Each document needs a type — the type decides who may read it.",
    ar: "أرفق مستندات هذا العضو. لكل مستند نوع — والنوع يحدّد من يمكنه الاطلاع عليه.",
  },
  type: { en: "Document type", ar: "نوع المستند" },
  choose: { en: "Choose a type", ar: "اختر نوعًا" },
  file: { en: "File", ar: "الملف" },
  docTitle: { en: "Title", ar: "العنوان" },
  docDate: { en: "Date on the document", ar: "تاريخ المستند" },
  upload: { en: "Upload", ar: "رفع" },
  uploaded: { en: "Document filed.", ar: "تم حفظ المستند." },
  none: { en: "No documents on file yet.", ar: "لا توجد مستندات بعد." },
  needType: { en: "Choose a document type.", ar: "اختر نوع المستند." },
  needFile: { en: "Choose a file.", ar: "اختر ملفًا." },
  needTitle: { en: "A title is required.", ar: "العنوان مطلوب." },
  view: { en: "View", ar: "عرض" },
  download: { en: "Download", ar: "تنزيل" },
  uploadedBy: { en: "uploaded by", ar: "رفعه" },
  locked: { en: "Locked", ar: "مقيّد" },
  lockedHint: {
    en: "Your role may see that this document exists but not open it.",
    ar: "يمكن لدورك رؤية وجود هذا المستند دون فتحه.",
  },
  withdrawn: { en: "Withdrawn", ar: "مسحوب" },
  expired: { en: "Expired", ar: "منتهٍ" },
  verified: { en: "Verified", ar: "موثّق" },
  previewUnavailable: {
    en: "This file type cannot be shown in place. Download it to open it.",
    ar: "لا يمكن عرض هذا النوع هنا. نزّله لفتحه.",
  },
  close: { en: "Close", ar: "إغلاق" },
  photoHint: {
    en: "A personal photo becomes the member's picture on their file. It is stored only once a consent covering photography is on record.",
    ar: "تصبح الصورة الشخصية صورة العضو في ملفه. ولا تُحفظ إلا بوجود موافقة على التصوير.",
  },
} satisfies Record<string, Localized>;

/**
 * The operator's word for a document, and the server class that carries its rules.
 *
 * `clinical` marks the two that carry a clinical floor: an administrative role may FILE them (a registration
 * officer receives the paperwork) and will not be able to open them back, which is the same rule that governs
 * a scanned lab result today.
 */
export const DOCUMENT_TYPES: ReadonlyArray<{
  documentClass: string;
  label: Localized;
  clinical?: boolean;
  note?: Localized;
}> = [
  { documentClass: "MedicalReport", label: { en: "Medical file", ar: "ملف طبي" }, clinical: true },
  { documentClass: "LabResult", label: { en: "Investigations", ar: "الفحوصات" }, clinical: true },
  { documentClass: "CardCopy", label: { en: "Card copy", ar: "صورة البطاقة" } },
  { documentClass: "PolicyContract", label: { en: "Policy document", ar: "وثيقة التأمين" } },
  { documentClass: "IdentityDocument", label: { en: "Personal document", ar: "مستند شخصي" } },
  { documentClass: "CaseDocument", label: { en: "Case document", ar: "مستند الحالة" } },
  { documentClass: "IdentityPhoto", label: { en: "Personal photo", ar: "صورة شخصية" }, note: S.photoHint },
];

const classLabel = (documentClass: string): Localized =>
  DOCUMENT_TYPES.find((t) => t.documentClass === documentClass)?.label ?? { en: documentClass, ar: documentClass };

export function BeneficiaryDocuments({
  enrollmentId,
  api = httpPolicyApi,
}: {
  /** Documents hang off the MEMBERSHIP, which is where the server scopes and authorizes them. */
  enrollmentId: string;
  api?: PolicyApi;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [docs, setDocs] = useState<PolicyDocumentView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  const [preview, setPreview] = useState<{ doc: PolicyDocumentView; url: string } | null>(null);

  const load = useCallback(async () => {
    try {
      setDocs(await api.documents("enrollments", enrollmentId));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api, enrollmentId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function open(doc: PolicyDocumentView, purpose: "preview" | "download") {
    setError(null);
    try {
      const { url } = await api.documentDownloadUrl(doc.linkId, purpose);
      if (purpose === "download") window.open(url, "_blank", "noopener,noreferrer");
      else setPreview({ doc, url });
    } catch (e) {
      setError(writeErrorMessage(e).message);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div>
        <h3 style={{ margin: 0 }}>{t(S.title)}</h3>
        <p className="muted" style={{ margin: "var(--sp1) 0 0" }}>{t(S.intro)}</p>
      </div>

      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <DocumentUpload
        api={api}
        enrollmentId={enrollmentId}
        onUploaded={async () => {
          setAnnounce(t(S.uploaded));
          await load();
        }}
      />

      {docs && docs.length === 0 && <InlineAlert tone="info">{t(S.none)}</InlineAlert>}

      <ul className="ben-docs">
        {(docs ?? []).map((d) => (
          <li key={d.linkId} className="ben-doc">
            <div className="ben-doc-body">
              <div className="ben-doc-head">
                <strong className="ben-doc-name">{d.title}</strong>
                <StatusChip kind="neu" label={t(classLabel(d.documentClass))} />
                {d.status === "Withdrawn" && <StatusChip kind="bad" label={t(S.withdrawn)} />}
                {d.expired && <StatusChip kind="warn" label={t(S.expired)} />}
                {d.verifiedAt && <StatusChip kind="ok" label={t(S.verified)} />}
              </div>
              {/* The date sits UNDER the name, with the uploader beside it — the three facts an officer
                  checks before acting on a document, in the order they ask for them. */}
              <div className="ben-doc-sub">
                <span className="tnum">{fmt.dateTime(d.uploadedAt)}</span>
                <span aria-hidden>·</span>
                <span>{t(S.uploadedBy)} {d.uploadedByDisplay}</span>
                {d.versionNo > 1 && <span className="tnum">· v{d.versionNo}</span>}
              </div>
            </div>

            <div className="ben-doc-actions">
              {d.canDownload ? (
                <>
                  {/* Icon-only, so both carry a real accessible name naming the DOCUMENT — "View" alone in a
                      list of nine documents tells a screen-reader user nothing about which one. */}
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={`${t(S.view)} — ${d.title}`}
                    onClick={() => void open(d, "preview")}
                  >
                    <Icon name="eye" aria-hidden />
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={`${t(S.download)} — ${d.title}`}
                    onClick={() => void open(d, "download")}
                  >
                    <Icon name="download" aria-hidden />
                  </Button>
                </>
              ) : (
                // Named, not absent: an empty cell reads as a broken screen rather than as a rule.
                <span className="pol-locked-inline" title={t(S.lockedHint)}>
                  <Icon name="info" aria-hidden /> {t(S.locked)}
                </span>
              )}
            </div>
          </li>
        ))}
      </ul>

      {preview && (
        <DocumentPreview doc={preview.doc} url={preview.url} onClose={() => setPreview(null)} />
      )}
    </Card>
  );
}

// ── Upload ──────────────────────────────────────────────────────────────────────────────────────────────

function DocumentUpload({
  api,
  enrollmentId,
  onUploaded,
}: {
  api: PolicyApi;
  enrollmentId: string;
  onUploaded: () => Promise<void>;
}) {
  const t = useLoc();
  const [documentClass, setDocumentClass] = useState<string | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState("");
  const [documentDate, setDocumentDate] = useState("");
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const chosen = DOCUMENT_TYPES.find((x) => x.documentClass === documentClass);
  const options = useMemo(
    () => DOCUMENT_TYPES.map((x) => ({ value: x.documentClass, label: t(x.label) })),
    [t],
  );

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setTouched(true);
    setError(null);
    if (!documentClass || !file || title.trim() === "") return;

    setBusy(true);
    try {
      await api.attachDocument(
        "enrollments",
        enrollmentId,
        file,
        { documentClass, title: title.trim(), documentDate: documentDate || undefined },
      );
      // Cleared only on a CONFIRMED success — wiping the form after a failure destroys the operator's typing
      // and, worse, leaves them unsure whether the file went up.
      setDocumentClass(null);
      setFile(null);
      setTitle("");
      setDocumentDate("");
      setTouched(false);
      if (fileInput.current) fileInput.current.value = "";
      await onUploaded();
    } catch (err) {
      setError(writeErrorMessage(err).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} noValidate className="ben-doc-upload" aria-label={t(S.title)}>
      <div className="mrs-field">
        <label className="mrs-label" id="doc-type-label">{t(S.type)} *</label>
        <Select
          aria-labelledby="doc-type-label"
          options={options}
          value={documentClass}
          onChange={setDocumentClass}
          placeholder={t(S.choose)}
        />
        {touched && !documentClass && <span className="mrs-error">{t(S.needType)}</span>}
      </div>

      <InputField
        label={`${t(S.docTitle)} *`}
        value={title}
        error={touched && title.trim() === "" ? t(S.needTitle) : undefined}
        onChange={(e) => setTitle(e.currentTarget.value)}
        autoComplete="off"
      />

      <InputField
        type="date"
        label={t(S.docDate)}
        value={documentDate}
        onChange={(e) => setDocumentDate(e.currentTarget.value)}
      />

      <div className="mrs-field">
        <label className="mrs-label" htmlFor="doc-file">{t(S.file)} *</label>
        <input
          ref={fileInput}
          id="doc-file"
          className="mrs-control"
          type="file"
          accept=".pdf,.png,.jpg,.jpeg,.webp,.tif,.tiff,.doc,.docx"
          onChange={(e) => setFile(e.target.files?.[0] ?? null)}
        />
        {touched && !file && <span className="mrs-error">{t(S.needFile)}</span>}
      </div>

      {/* The consent rule for a photograph is stated BEFORE the upload is attempted. The server enforces it
          either way; saying so here turns a 422 into something the operator could have known. */}
      {chosen?.note && (
        <div className="ben-doc-upload-wide">
          <InlineAlert tone="info">{t(chosen.note)}</InlineAlert>
        </div>
      )}
      {error && (
        <div className="ben-doc-upload-wide">
          <InlineAlert tone="bad">{t(error)}</InlineAlert>
        </div>
      )}

      <div className="ben-doc-upload-wide">
        <Button type="submit" variant="secondary" loading={busy}>{t(S.upload)}</Button>
      </div>
    </form>
  );
}

// ── Preview ─────────────────────────────────────────────────────────────────────────────────────────────

/** Extensions we can render in place. Anything else is offered as a download rather than shown in a frame
 *  that would render a browser error where the document should be. */
const IMAGE = /\.(png|jpe?g|webp|gif|bmp)(\?|$)/i;
const PDF = /\.pdf(\?|$)/i;

function DocumentPreview({
  doc,
  url,
  onClose,
}: {
  doc: PolicyDocumentView;
  url: string;
  onClose: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  // The signed URL carries a query string, so the extension is read from the TITLE as well — a store that
  // signs with an opaque path would otherwise make every document unpreviewable.
  const source = `${doc.title} ${url}`;
  const isImage = IMAGE.test(source);
  const isPdf = PDF.test(source);

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={doc.title}
      footer={<Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>}
    >
      <p className="muted" style={{ marginTop: 0 }}>
        <span className="tnum">{fmt.dateTime(doc.uploadedAt)}</span> · {t(S.uploadedBy)} {doc.uploadedByDisplay}
      </p>

      {isImage && (
        // `referrerPolicy` so the signed URL is never leaked in a Referer header to whatever the store
        // redirects to. The signature is short-lived, but a short-lived credential in a log is still one.
        <img
          className="ben-doc-preview"
          src={url}
          alt={doc.title}
          referrerPolicy="no-referrer"
        />
      )}
      {isPdf && (
        <iframe className="ben-doc-preview" src={url} title={doc.title} referrerPolicy="no-referrer" />
      )}
      {!isImage && !isPdf && <InlineAlert tone="info">{t(S.previewUnavailable)}</InlineAlert>}
    </Modal>
  );
}
