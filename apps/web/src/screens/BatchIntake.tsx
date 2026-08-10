import { useEffect, useMemo, useState } from "react";
import { Button, Card, ComboboxField, DataTable, Icon, InlineAlert, KpiList, StatusChip, useTheme } from "@mersal/design-system";
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
import { writeErrorMessage } from "../api/writeError";
import { useLoc, readErrorMessage } from "./_shared";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { useRegistrationReference } from "./useRegistrationReference";
import { BulkTemplateActions } from "./BulkTemplateActions";
import { BulkErrorReportButton } from "./BulkErrorReport";

/** ONE client for the module — see the note in BeneficiaryPortal. */
const httpPolicyApi = createHttpPolicyApi();

/**
 * Register hundreds of members from one file.
 *
 * ============================================================================================================
 * WHY THIS IS THE SAME ENGINE, NOT A SECOND ONE
 * ============================================================================================================
 * Members arrive from UNHCR in batches of hundreds. Typing them one at a time is not a workflow, and a second
 * import path would be a second set of rules about what a valid member is — which is how two doors into one
 * registry come to disagree. So this drives the SAME upload → validate → commit → reconcile pipeline that
 * Bulk & Imports drives, with the same guarantee: nothing is applied until commit, and commit is unreachable
 * until the dry run has answered.
 *
 * ============================================================================================================
 * RE-UPLOADING A CORRECTED FILE IS THE NORMAL CASE
 * ============================================================================================================
 * An operator fixes the rows the error report named and uploads the whole file again — not a hand-edited
 * subset, which is how one member ends up with two records. The engine keys each row on the CARD NUMBER, so
 * an unchanged row reports as skipped and only what actually changed is written. That is stated on screen,
 * because an operator who does not believe it will edit the file down by hand instead.
 */

const S = {
  title: { en: "Register many from a file", ar: "تسجيل عدة أعضاء من ملف" },
  intro: {
    en: "Upload a file, check what it would do, then commit. Nothing is written until you commit.",
    ar: "ارفع الملف، وراجع ما سيفعله، ثم نفّذ. لا يُكتب أي شيء قبل التنفيذ.",
  },
  reupload: {
    en: "Fix the rows below and upload the whole file again — members are matched on their card number, so rows that have not changed are skipped rather than duplicated.",
    ar: "صحّح الصفوف أدناه وأعد رفع الملف كاملًا — تتم مطابقة الأعضاء برقم البطاقة، فتُتجاوز الصفوف غير المتغيّرة بدل تكرارها.",
  },
  template: { en: "Download the template", ar: "تنزيل القالب" },
  templateHint: {
    en: "Use the template. An unknown or missing column fails the whole file — the engine will not guess what a column means.",
    ar: "استخدم القالب. أي عمود غير معروف أو ناقص يُفشل الملف بأكمله — لا يخمّن النظام معنى العمود.",
  },
  defaults: { en: "Shared for this batch", ar: "مشترك لهذه الدفعة" },
  defaultsHint: {
    en: "Applied to any row that leaves the column blank, so the common case is entered once. A row that names its own value keeps it — contribution is per member and is not offered here.",
    ar: "يُطبَّق على أي صف يترك العمود فارغًا، ليُدخَل الشائع مرة واحدة. ويحتفظ الصف بقيمته الخاصة إن حدّدها — والمشاركة لكل عضو ولا تُحدَّد هنا.",
  },
  file: { en: "File (CSV or XLSX)", ar: "الملف (CSV أو XLSX)" },
  upload: { en: "Upload", ar: "رفع" },
  validate: { en: "Check the file (dry run)", ar: "فحص الملف (تشغيل تجريبي)" },
  commit: { en: "Register these members", ar: "تسجيل هؤلاء الأعضاء" },
  columns: { en: "Expected columns", ar: "الأعمدة المتوقعة" },
  column: { en: "Column", ar: "العمود" },
  required: { en: "Required", ar: "مطلوب" },
  meaning: { en: "Meaning", ar: "المعنى" },
  submitted: { en: "Rows in the file", ar: "صفوف الملف" },
  valid: { en: "Ready", ar: "جاهزة" },
  invalid: { en: "Need fixing", ar: "تحتاج تصحيحًا" },
  applied: { en: "Registered", ar: "مسجَّلة" },
  failed: { en: "Failed", ar: "فشلت" },
  skipped: { en: "Unchanged", ar: "دون تغيير" },
  rowNo: { en: "Row", ar: "الصف" },
  code: { en: "Code", ar: "الرمز" },
  detail: { en: "Reason", ar: "السبب" },
  nothingApplied: {
    en: "Nothing has been registered. The check is a dry run — committing is what writes.",
    ar: "لم يُسجَّل أي شيء. الفحص تشغيل تجريبي — والتنفيذ هو ما يكتب.",
  },
  wouldChange: { en: "What this file would do", ar: "ما سيفعله هذا الملف" },
  committable: { en: "This file is ready to register.", ar: "الملف جاهز للتسجيل." },
  notCommittable: {
    en: "This file cannot be registered yet. Fix the rows above and upload it again.",
    ar: "لا يمكن تسجيل هذا الملف بعد. صحّح الصفوف أعلاه وأعد الرفع.",
  },
  balanced: { en: "Every row in the file is accounted for.", ar: "كل صف في الملف محسوب." },
  unbalanced: {
    en: "The counts do not add up. A job that cannot say what happened to a row is one that lost it — raise this.",
    ar: "الأعداد غير متطابقة. المهمة التي لا تستطيع بيان مصير صف هي مهمة فقدته — أبلغ عن ذلك.",
  },
  reconcile: { en: "Reconciliation", ar: "التسوية" },
  infected: {
    en: "This file failed the malware scan and was never read.",
    ar: "فشل هذا الملف في فحص البرمجيات الخبيثة ولم تتم قراءته.",
  },
  partial: {
    en: "Some rows failed and the rest were registered. That is the designed outcome, not an error — the report below says which.",
    ar: "فشلت بعض الصفوف وسُجِّل الباقي. هذه هي النتيجة المقصودة لا خطأ — يوضّح التقرير أدناه أيّها.",
  },
  plan: { en: "Plan", ar: "الخطة" },
  networkTier: { en: "Network tier", ar: "شريحة الشبكة" },
  defaultBranch: { en: "Default branch", ar: "الفرع الافتراضي" },
  choose: { en: "Leave to the file", ar: "اتركه للملف" },
  errorFile: {
    en: "The error report contains member data and is downloaded through an authorized, audited request.",
    ar: "يحتوي تقرير الأخطاء على بيانات أعضاء ويُنزَّل عبر طلب مصرّح به ومُدقَّق.",
  },
  changesTruncated: {
    en: "Showing the first {shown} of {total} changes.",
    ar: "يتم عرض أول {shown} من أصل {total} تغيير.",
  },
  errorsTruncated: {
    en: "Showing the first {shown} of {total} errors. Fixing only these will not make the file pass.",
    ar: "يتم عرض أول {shown} من أصل {total} خطأ. إصلاح هذه وحدها لن يجعل الملف يمرّ.",
  },
} satisfies Record<string, Localized>;

/** Registering and enrolling members is one job type — the file describes a person AND their coverage. */
const JOB_TYPE = "MemberEnrolment";

function jobStatusKind(status: string): "ok" | "warn" | "bad" | "neu" | "info" {
  switch (status) {
    case "Completed": return "ok";
    case "Failed": return "bad";
    case "RolledBack": return "warn";
    case "Validated": return "info";
    default: return "neu";
  }
}

export function BatchIntake({ api = httpPolicyApi }: { api?: PolicyApi } = {}) {
  const t = useLoc();
  const fmt = useFormat();
  const { lang } = useTheme();
  const reference = useRegistrationReference(api);

  const [templates, setTemplates] = useState<BulkTemplateView[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [job, setJob] = useState<BulkJobView | null>(null);
  const [validation, setValidation] = useState<BulkValidationView | null>(null);
  const [commit, setCommit] = useState<BulkCommitView | null>(null);
  const [recon, setRecon] = useState<BulkReconciliationView | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [announce, setAnnounce] = useState("");
  /** Controlled so a validation failure can reopen the column contract — see BulkTemplateActions. */
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [uploadKey, rotateUploadKey] = useIdempotencyKey();
  const [commitKey, rotateCommitKey] = useIdempotencyKey();

  // Batch defaults. Contribution is deliberately absent: it is the one value that varies member by member
  // inside an otherwise shared batch, so offering a single value for it would invite exactly the mistake of
  // applying one person's share to everybody.
  const [planId, setPlanId] = useState<string | null>(null);
  const [tierId, setTierId] = useState<string | null>(null);
  const [branchId, setBranchId] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.bulkTemplates().then((r) => live && setTemplates(r)).catch(() => setTemplates([]));
    return () => { live = false; };
  }, [api]);

  const template = templates.find((x) => x.jobType === JOB_TYPE) ?? null;

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
      // The defaults are recorded ON THE JOB, so validate and commit apply the same ones. Sending them per
      // request instead would let the dry run preview one batch and the commit write another.
      const j = await api.uploadBulk(JOB_TYPE, file, uploadKey, {
        planId, networkTierId: tierId, branchId,
      });
      rotateUploadKey();
      setJob(j);
      // A file that failed the scan arrives as a Failed job with a code rather than as an exception: the job
      // EXISTS, and the operator needs to see that it was rejected and why.
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

  /*
    Held whole, so the inline list can be reported against its real size. The server caps the inline errors at
    50 (BulkJobEngine.InlineErrorLimit) — the full list names people and lives in the stored report — and
    returns `totalErrors` beside them. Rendering the 50 without that count reads as "these are the errors".
  */
  const report = commit ?? validation ?? null;
  const errors = report?.errors ?? [];
  const totalErrors = report?.totalErrors ?? 0;

  // LOCALIZED labels, and `keywords` so the code is searchable without being read out as the answer.
  //
  // These three read `nameEn` unconditionally until the scrolls/dropdowns audit — an Arabic operator got an
  // English plan list on the batch-enrolment screen, with `nameAr` sitting unused on all three schemas. It
  // had to be fixed WITH the conversion rather than after it: the combobox filters on `label`, so a
  // searchable list of English-only labels is a list an Arabic operator cannot search either.
  const planOptions = useMemo(
    () => reference.plans.map((p) => ({
      value: p.planId, label: t({ en: p.nameEn, ar: p.nameAr }), hint: p.planCode, keywords: p.planCode,
    })),
    [reference.plans, t],
  );
  const tierOptions = useMemo(
    () => reference.tiers.map((x) => ({
      value: x.networkTierId, label: t({ en: x.nameEn, ar: x.nameAr }), hint: x.tierCode, keywords: x.tierCode,
    })),
    [reference.tiers, t],
  );
  const branchOptions = useMemo(
    // `nameAr` is optional on a branch reference, so English is the stated fallback rather than a blank name.
    () => reference.branches.map((b) => ({
      value: b.branchId, label: t({ en: b.nameEn, ar: b.nameAr ?? b.nameEn }),
    })),
    [reference.branches, t],
  );

  return (
    <div className="ben-batch">
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {reference.unavailable && <InlineAlert tone="warn">{t(reference.unavailable)}</InlineAlert>}

      <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
        <div>
          <h3 style={{ margin: 0 }}>{t(S.title)}</h3>
          <p className="muted" style={{ margin: "var(--sp1) 0 0" }}>{t(S.intro)}</p>
        </div>

        {/* The hint stays inline — one sentence, and the one that prevents a failed upload. The column
            TABLE moved into the modal behind the paired trigger (0B §11). */}
        <InlineAlert tone="info">{t(S.templateHint)}</InlineAlert>
        <BulkTemplateActions
          jobType={JOB_TYPE}
          template={template ?? null}
          open={columnsOpen}
          onOpenChange={setColumnsOpen}
        />

        {/* ---- Shared coverage -------------------------------------------------------------------- */}
        <fieldset className="ben-section">
          <legend>{t(S.defaults)}</legend>
          <p className="ben-section-hint">{t(S.defaultsHint)}</p>
          <div className="ben-batch-defaults">
            <ComboboxField
              id="batch-plan" label={t(S.plan)} options={planOptions} hintWhenClosed
              value={planId} onChange={setPlanId} placeholder={t(S.choose)} disabled={reference.loading}
            />
            <ComboboxField
              id="batch-tier" label={t(S.networkTier)} options={tierOptions} hintWhenClosed
              value={tierId} onChange={setTierId} placeholder={t(S.choose)} disabled={reference.loading}
            />
            <ComboboxField
              id="batch-branch" label={t(S.defaultBranch)} options={branchOptions}
              value={branchId} onChange={setBranchId} placeholder={t(S.choose)}
            />
          </div>
        </fieldset>

        <div className="mrs-field" style={{ maxWidth: 480 }}>
          <label className="mrs-label" htmlFor="batch-file">{t(S.file)}</label>
          <input
            className="mrs-control"
            id="batch-file"
            type="file"
            accept=".csv,.xlsx"
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          />
        </div>
        <div>
          <Button variant="primary"
              leadingIcon={<Icon name="upload" />} onClick={doUpload} loading={busy} disabled={!file || busy}>
            {t(S.upload)}
          </Button>
        </div>
      </Card>

      {job && (
        <Card data-testid="batch-job" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
          <div className="pol-editor-head">
            <h3>{job.fileName}</h3>
            <StatusChip kind={jobStatusKind(job.status)} label={job.status} />
          </div>
          {job.failureCode && (
            <InlineAlert tone="bad" data-testid="batch-failure">
              {job.failureCode === "FILE_INFECTED" ? t(S.infected) : job.failureDetail}
            </InlineAlert>
          )}

          {/* `KpiList`, not the `pol-kpis` definition list this used to be — the same migration the two
              utilization panels made. Six row counts are exactly what the KPI treatment is for, and on the
              screen an operator watches to decide whether to commit a file, "how many rows failed" should not
              be set smaller than the body copy beside it. Same definition-list semantics, same classes as
              `KpiCard`. */}
          <KpiList
            items={[
              { label: t(S.submitted), value: fmt.number(job.totalRows) },
              { label: t(S.valid), value: fmt.number(job.validRows) },
              { label: t(S.invalid), value: fmt.number(job.invalidRows) },
              { label: t(S.applied), value: fmt.number(job.appliedRows) },
              { label: t(S.failed), value: fmt.number(job.failedRows) },
              { label: t(S.skipped), value: fmt.number(job.skippedRows) },
            ]}
          />

          <div className="pol-editor-actions">
            <Button variant="secondary" onClick={doValidate} disabled={busy || job.status === "Failed"}>
              {t(S.validate)}
            </Button>
            {/* Gated on the dry run having said the file is committable. The server enforces the same
                transition; this only stops an operator reaching for it first. */}
            <Button variant="primary"
              leadingIcon={<Icon name="check2" />} onClick={doCommit} disabled={busy || !validation?.committable}>
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
            <>
              {/* Said here, next to the errors, because this is the moment the operator decides whether to
                  re-upload the whole file or hand-edit it down to the failing rows. */}
              <InlineAlert tone="info">{t(S.reupload)}</InlineAlert>
              {/* And this decides which of those two it is: hand-editing the rows on screen is only an option
                  when the rows on screen are all of them. */}
              {totalErrors > errors.length && (
                <InlineAlert tone="warn">
                  {t(S.errorsTruncated)
                    .replace("{shown}", fmt.number(errors.length))
                    .replace("{total}", fmt.number(totalErrors))}
                  {report?.job.errorDocumentId ? ` ${t(S.errorFile)}` : ""}
                </InlineAlert>
              )}
              <BulkErrorReportButton documentId={report?.job.errorDocumentId} jobId={report?.job.jobId ?? ""} />
              <DataTable
                caption={t(S.detail)}
                rows={errors}
                rowKey={(r) => String(r.rowNumber)}
                density="compact"
                columns={[
                  { key: "row", header: t(S.rowNo), cell: (r) => r.rowNumber, sortable: true, sortValue: (r) => r.rowNumber },
                  { key: "code", header: t(S.code), cell: (r) => <StatusChip kind="bad" label={r.code} />, sortable: true, sortValue: (r) => r.code },
                  { key: "detail", header: t(S.detail), cell: (r) => (lang === "ar" ? r.detailAr : r.detailEn) },
                ]}
              />
            </>
          )}

          {validation && validation.wouldChange.length > 0 && !commit && (
            <>
              <h4>{t(S.wouldChange)}</h4>
              {validation.totalWouldChange > validation.wouldChange.length && (
                <InlineAlert tone="info">
                  {t(S.changesTruncated)
                    .replace("{shown}", fmt.number(validation.wouldChange.length))
                    .replace("{total}", fmt.number(validation.totalWouldChange))}
                </InlineAlert>
              )}
              <DataTable
                caption={t(S.wouldChange)}
                rows={validation.wouldChange}
                rowKey={(r) => String(r.rowNumber)}
                density="compact"
                columns={[
                  { key: "row", header: t(S.rowNo), cell: (r) => r.rowNumber, sortable: true, sortValue: (r) => r.rowNumber },
                  { key: "summary", header: t(S.detail), cell: (r) => (lang === "ar" ? r.summaryAr : r.summaryEn) },
                ]}
              />
            </>
          )}

          {recon && (
            <div data-testid="batch-reconciliation">
              <h4>{t(S.reconcile)}</h4>
              <InlineAlert tone={recon.balances ? "ok" : "bad"}>
                {recon.balances ? t(S.balanced) : t(S.unbalanced)}
              </InlineAlert>
              <div className="pol-tablewrap mrs-scroll mrs-scroll-focusable" tabIndex={0}>
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
              </div>
              {recon.errorDocumentId && (
                <>
                  <InlineAlert tone="info">{t(S.errorFile)}</InlineAlert>
                  <BulkErrorReportButton documentId={recon.errorDocumentId} jobId={recon.jobId} />
                </>
              )}
            </div>
          )}
        </Card>
      )}

    </div>
  );
}
