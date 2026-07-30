import { useEffect, useState } from "react";
import { Button, Card, DataTable, InlineAlert, StatusChip, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  BulkCommitView,
  BulkJobView,
  BulkReconciliationView,
  BulkTemplateView,
  BulkValidationView,
  PolicyApi,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { BulkTemplateActions } from "./BulkTemplateActions";

/**
 * Phase 19.6 — the operator's side of the 19.5b bulk engine.
 *
 * The screen is the pipeline, in order, and it refuses to skip a step: upload → validate (DRY RUN) → commit
 * → reconcile. Commit is unreachable until validation has run, because the engine's own guarantee is that
 * NOTHING is applied until commit, and a UI that let an operator jump straight to commit would waste that
 * guarantee on the one file that needed it.
 *
 * Row errors are shown with their row number and the reason in the operator's own language. The people who
 * correct these files work in Arabic; an English-only reason means the fix is guessed from a code, and a
 * guessed fix to an enrolment file is somebody's cover.
 */

const S = {
  title: { en: "Bulk & Imports", ar: "الرفع الجماعي" },
  jobType: { en: "What are you uploading?", ar: "ما الذي ترفعه؟" },
  template: { en: "Download the template", ar: "تنزيل القالب" },
  templateHint: {
    en: "Use the template. An unknown or missing column fails the whole file — the engine will not guess what a column means.",
    ar: "استخدم القالب. أي عمود غير معروف أو ناقص يُفشل الملف بأكمله — لا يخمّن النظام معنى العمود.",
  },
  file: { en: "File (CSV or XLSX)", ar: "الملف (CSV أو XLSX)" },
  upload: { en: "Upload", ar: "رفع" },
  validate: { en: "Validate (dry run)", ar: "التحقق (تشغيل تجريبي)" },
  commit: { en: "Commit", ar: "التنفيذ" },
  reconcile: { en: "Reconciliation", ar: "التسوية" },
  columns: { en: "Expected columns", ar: "الأعمدة المتوقعة" },
  column: { en: "Column", ar: "العمود" },
  required: { en: "Required", ar: "مطلوب" },
  meaning: { en: "Meaning", ar: "المعنى" },
  status: { en: "Status", ar: "الحالة" },
  rows: { en: "Rows", ar: "الصفوف" },
  submitted: { en: "Submitted", ar: "المُرسل" },
  valid: { en: "Valid", ar: "صالح" },
  invalid: { en: "Invalid", ar: "غير صالح" },
  applied: { en: "Applied", ar: "مُطبَّق" },
  failed: { en: "Failed", ar: "فشل" },
  skipped: { en: "Skipped", ar: "متجاوَز" },
  rowNo: { en: "Row", ar: "الصف" },
  code: { en: "Code", ar: "الرمز" },
  detail: { en: "Reason", ar: "السبب" },
  nothingApplied: {
    en: "Nothing has been applied. Validation is a dry run — commit is what writes.",
    ar: "لم يتم تطبيق أي شيء. التحقق تشغيل تجريبي — التنفيذ هو ما يكتب.",
  },
  wouldChange: { en: "What this file would change", ar: "ما سيغيّره هذا الملف" },
  committable: { en: "This file can be committed.", ar: "يمكن تنفيذ هذا الملف." },
  notCommittable: {
    en: "This file cannot be committed yet. Fix the rows above and upload it again.",
    ar: "لا يمكن تنفيذ هذا الملف بعد. صحّح الصفوف أعلاه وأعد الرفع.",
  },
  balanced: { en: "Every submitted row is accounted for.", ar: "كل صف مُرسل محسوب." },
  unbalanced: {
    en: "The counts do not add up. A job that cannot say what happened to a row is one that lost it — raise this.",
    ar: "الأعداد غير متطابقة. المهمة التي لا تستطيع بيان مصير صف هي مهمة فقدته — أبلغ عن ذلك.",
  },
  errorFile: {
    en: "The error report contains member data and is downloaded through an authorized, audited request.",
    ar: "يحتوي تقرير الأخطاء على بيانات أعضاء ويُنزَّل عبر طلب مصرّح به ومُدقَّق.",
  },
  infected: {
    en: "This file failed the malware scan and was never parsed.",
    ar: "فشل هذا الملف في فحص البرمجيات الخبيثة ولم تتم قراءته.",
  },
  jobs: { en: "Recent jobs", ar: "المهام الأخيرة" },
  fileName: { en: "File", ar: "الملف" },
  uploaded: { en: "Uploaded", ar: "تاريخ الرفع" },
  partial: {
    en: "Some rows failed and the rest were applied. That is the designed outcome, not an error — the report below says which.",
    ar: "فشلت بعض الصفوف وطُبِّق الباقي. هذه هي النتيجة المقصودة لا خطأ — يوضّح التقرير أدناه أيّها.",
  },
} satisfies Record<string, Localized>;

const JOB_TYPES = [
  "MemberEnrolment",
  "MemberTermination",
  "PlanChange",
  "GroupAssignment",
  "ContactUpdate",
  "ProviderTierAssignment",
  "BenefitRuleImport",
];

function jobStatusKind(status: string): "ok" | "warn" | "bad" | "neu" | "info" {
  switch (status) {
    case "Completed": return "ok";
    case "Failed": return "bad";
    case "RolledBack": return "warn";
    case "Validated": return "info";
    default: return "neu";
  }
}

export function BulkJobs({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const { lang } = useTheme();
  const [templates, setTemplates] = useState<BulkTemplateView[]>([]);
  const [jobType, setJobType] = useState(JOB_TYPES[0]);
  const [file, setFile] = useState<File | null>(null);
  const [job, setJob] = useState<BulkJobView | null>(null);
  const [validation, setValidation] = useState<BulkValidationView | null>(null);
  const [commit, setCommit] = useState<BulkCommitView | null>(null);
  const [recon, setRecon] = useState<BulkReconciliationView | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [announce, setAnnounce] = useState("");
  // Controlled open for the column-contract modal. `BulkTemplateActions` manages its own open state by
  // default; the parent holds it too so a validation failure ("unknown column: xyz") can reopen the contract
  // straight from the alert rather than making the operator find the trigger again.
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [uploadKey, rotateUploadKey] = useIdempotencyKey();
  const [commitKey, rotateCommitKey] = useIdempotencyKey();

  useEffect(() => {
    let live = true;
    api.bulkTemplates().then((r) => live && setTemplates(r)).catch(() => setTemplates([]));
    return () => { live = false; };
  }, [api]);

  const template = templates.find((x) => x.jobType === jobType) ?? null;

  function reset() {
    setJob(null);
    setValidation(null);
    setCommit(null);
    setRecon(null);
  }

  async function doUpload() {
    if (!file) return;
    setBusy(true);
    setError(null);
    reset();
    try {
      const j = await api.uploadBulk(jobType, file, uploadKey);
      rotateUploadKey();
      setJob(j);
      // A file that failed the scan reaches this screen as a Failed job with a code. It is deliberately not
      // an exception: the job EXISTS, and the operator needs to see that it was rejected and why.
      setAnnounce(j.status === "Failed" ? t(S.infected) : t(S.nothingApplied));
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function doValidate() {
    if (!job) return;
    setBusy(true);
    setError(null);
    try {
      const v = await api.validateBulk(job.jobId);
      setValidation(v);
      setJob(v.job);
      setAnnounce(t(S.nothingApplied));
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function doCommit() {
    if (!job) return;
    setBusy(true);
    setError(null);
    try {
      const c = await api.commitBulk(job.jobId, commitKey);
      rotateCommitKey();
      setCommit(c);
      setJob(c.job);
      setRecon(await api.bulkReconciliation(job.jobId));
      setAnnounce(t(S.reconcile));
    } catch (e) {
      setError(readErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const errors = commit?.errors ?? validation?.errors ?? [];

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <Card style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
        {/* QA P1-10: this card was raw browser controls crammed into one unspaced row while every other
            screen wears the design system — the controls now use the shared field classes and breathe. */}
        <div className="mrs-field" style={{ maxWidth: 360 }}>
          <label className="mrs-label" htmlFor="bulk-type">{t(S.jobType)}</label>
          <select className="mrs-control" id="bulk-type" value={jobType} onChange={(e) => { setJobType(e.target.value); reset(); }}>
            {JOB_TYPES.map((x) => (
              <option key={x} value={x}>
                {x}
              </option>
            ))}
          </select>
        </div>

        {/* Hint stays inline; the column TABLE lives in the modal behind the paired trigger (0B §11). */}
        <InlineAlert tone="info">{t(S.templateHint)}</InlineAlert>
        <BulkTemplateActions
          jobType={jobType}
          template={template ?? null}
          open={columnsOpen}
          onOpenChange={setColumnsOpen}
        />

        <div className="mrs-field" style={{ maxWidth: 480 }}>
          <label className="mrs-label" htmlFor="bulk-file">{t(S.file)}</label>
          <input
            className="mrs-control"
            id="bulk-file"
            type="file"
            accept=".csv,.xlsx"
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          />
        </div>
        <div>
          <Button variant="primary" onClick={doUpload} loading={busy} disabled={!file || busy}>
            {t(S.upload)}
          </Button>
        </div>
      </Card>

      {job && (
        <Card data-testid="bulk-job">
          <div className="pol-editor-head">
            <h3>{job.fileName}</h3>
            <StatusChip kind={jobStatusKind(job.status)} label={job.status} />
          </div>
          {job.failureCode && (
            <InlineAlert tone="bad" data-testid="bulk-failure">
              {job.failureCode === "FILE_INFECTED" ? t(S.infected) : job.failureDetail}
            </InlineAlert>
          )}
          <dl className="pol-kpis">
            <div><dt>{t(S.submitted)}</dt><dd>{fmt.number(job.totalRows)}</dd></div>
            <div><dt>{t(S.valid)}</dt><dd>{fmt.number(job.validRows)}</dd></div>
            <div><dt>{t(S.invalid)}</dt><dd>{fmt.number(job.invalidRows)}</dd></div>
            <div><dt>{t(S.applied)}</dt><dd>{fmt.number(job.appliedRows)}</dd></div>
            <div><dt>{t(S.failed)}</dt><dd>{fmt.number(job.failedRows)}</dd></div>
            <div><dt>{t(S.skipped)}</dt><dd>{fmt.number(job.skippedRows)}</dd></div>
          </dl>

          <div className="pol-editor-actions">
            <Button variant="secondary" onClick={doValidate} disabled={busy || job.status === "Failed"}>
              {t(S.validate)}
            </Button>
            {/* Commit is gated on the dry run having said the file is committable — the server enforces the
                same transition, this only stops an operator reaching for it first. */}
            <Button variant="primary" onClick={doCommit} disabled={busy || !validation?.committable}>
              {t(S.commit)}
            </Button>
          </div>

          {validation && !commit && (
            <InlineAlert tone={validation.committable ? "ok" : "warn"}>
              {validation.committable ? t(S.committable) : t(S.notCommittable)}
            </InlineAlert>
          )}
          {validation && !commit && <InlineAlert tone="info">{t(S.nothingApplied)}</InlineAlert>}
          {commit && commit.job.failedRows > 0 && <InlineAlert tone="warn">{t(S.partial)}</InlineAlert>}

          {errors.length > 0 && (
            <DataTable
              caption={t(S.detail)}
              rows={errors}
              rowKey={(r) => String(r.rowNumber)}
              density="compact"
              columns={[
                { key: "row", header: t(S.rowNo), cell: (r) => r.rowNumber },
                { key: "code", header: t(S.code), cell: (r) => <StatusChip kind="bad" label={r.code} /> },
                { key: "detail", header: t(S.detail), cell: (r) => (lang === "ar" ? r.detailAr : r.detailEn) },
              ]}
            />
          )}

          {validation && validation.wouldChange.length > 0 && !commit && (
            <>
              <h4>{t(S.wouldChange)}</h4>
              <DataTable
                caption={t(S.wouldChange)}
                rows={validation.wouldChange}
                rowKey={(r) => String(r.rowNumber)}
                density="compact"
                columns={[
                  { key: "row", header: t(S.rowNo), cell: (r) => r.rowNumber },
                  { key: "summary", header: t(S.detail), cell: (r) => (lang === "ar" ? r.summaryAr : r.summaryEn) },
                ]}
              />
            </>
          )}

          {recon && (
            <div data-testid="bulk-reconciliation">
              <h4>{t(S.reconcile)}</h4>
              <InlineAlert tone={recon.balances ? "ok" : "bad"}>
                {recon.balances ? t(S.balanced) : t(S.unbalanced)}
              </InlineAlert>
              <table className="pol-costshare">
                <caption className="sr-only">{t(S.reconcile)}</caption>
                <tbody>
                  <tr><th scope="row">{t(S.submitted)}</th><td>{fmt.number(recon.submitted)}</td></tr>
                  <tr><th scope="row">{t(S.valid)}</th><td>{fmt.number(recon.valid)}</td></tr>
                  <tr><th scope="row">{t(S.invalid)}</th><td>{fmt.number(recon.invalid)}</td></tr>
                  <tr><th scope="row">{t(S.applied)}</th><td>{fmt.number(recon.applied)}</td></tr>
                  <tr><th scope="row">{t(S.failed)}</th><td>{fmt.number(recon.failed)}</td></tr>
                  <tr><th scope="row">{t(S.skipped)}</th><td>{fmt.number(recon.skipped)}</td></tr>
                </tbody>
              </table>
              {recon.errorDocumentId && <InlineAlert tone="info">{t(S.errorFile)}</InlineAlert>}
            </div>
          )}
        </Card>
      )}
    </div>
  );
}
