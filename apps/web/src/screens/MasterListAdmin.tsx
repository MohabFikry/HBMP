import { useMemo, useState } from "react";
import {
  Button, Card, DataTable, Icon, InlineAlert, InputField, SelectField, StatusChip, TextareaField, useToast,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { CLINICAL_CODE_SYSTEMS } from "@mersal/contracts";
import type {
  ClinicalCodeSystem, Localized, MasterDataAsOf, MasterDataVersion,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { writeErrorMessage } from "../api/writeError";
import { useFormat } from "../i18n/useFormat";
import { useTheme } from "@mersal/design-system";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Master lists", ar: "القوائم المرجعية" },
  lede: {
    en: "The clinical vocabularies the platform reads: diagnoses, procedures, lab analytes and drug classes. "
      + "A wrong entry here does not fail loudly — it misroutes a diagnosis, or breaks an interaction check, "
      + "and the first sign of it is a decision that arrives in your queue looking wrong.",
    ar: "المفردات السريرية التي تعتمدها المنصّة: التشخيصات والإجراءات وتحاليل المختبر وفئات الأدوية. الخطأ "
      + "هنا لا يظهر مباشرة — بل يسيء توجيه تشخيص أو يعطّل فحص التداخلات، وأول ما تلاحظه هو قرار يصلك "
      + "في قائمتك يبدو خاطئاً.",
  },
  appendOnly: {
    en: "An edit APPENDS a version; it never overwrites one. A prescription written last March still resolves "
      + "this code as it read last March — which is why there is no delete, and why retiring a code is itself "
      + "recorded as a version.",
    ar: "التعديل يضيف إصداراً جديداً ولا يستبدل السابق. فالوصفة المكتوبة في مارس الماضي تظل تقرأ هذا الرمز "
      + "كما كان حينها — ولهذا لا يوجد حذف، وإيقاف الرمز يُسجَّل هو نفسه كإصدار.",
  },
  scope: {
    en: "Clinical governance edits ICD-10, CPT, LOINC and ATC. The administrative vocabularies — formulary "
      + "tiers, allergen groupings — stay with the platform administrators.",
    ar: "تشمل الحوكمة السريرية ICD-10 وCPT وLOINC وATC. أما المفردات الإدارية — فئات القائمة الدوائية "
      + "وتجميعات المُحسِّسات — فتبقى لدى مديري المنصّة.",
  },

  // ---- the table ----
  inForce: { en: "In force", ar: "السارية" },
  system: { en: "System", ar: "النظام" },
  code: { en: "Code", ar: "الرمز" },
  version: { en: "Version", ar: "الإصدار" },
  status: { en: "Status", ar: "الحالة" },
  effective: { en: "Effective from", ar: "سارٍ من" },
  rationale: { en: "Rationale", ar: "المبرر" },
  active: { en: "Active", ar: "نشط" },
  retired: { en: "Retired", ar: "موقوف" },
  empty: { en: "No master-data versions in force.", ar: "لا توجد إصدارات بيانات مرجعية سارية." },
  edit: { en: "Edit", ar: "تعديل" },
  editNamed: { en: "Edit — {code}", ar: "تعديل — {code}" },

  // ---- the editor ----
  newVersion: { en: "New version", ar: "إصدار جديد" },
  attributes: { en: "Attributes", ar: "الخصائص" },
  attributesHint: {
    en: "One per line, as name = value. This is the snapshot the platform reads for this code.",
    ar: "واحدة في كل سطر بصيغة الاسم = القيمة. هذه هي اللقطة التي تقرأها المنصّة لهذا الرمز.",
  },
  attributesInvalid: {
    en: "Each line must read name = value. Fix the highlighted lines: {lines}",
    ar: "يجب أن يكون كل سطر بصيغة الاسم = القيمة. صحّح الأسطر: {lines}",
  },
  rationaleLabel: { en: "Why this change", ar: "سبب هذا التغيير" },
  rationaleHint: {
    en: "Required. This is what somebody reads in three years asking why this code changed the week a claim "
      + "was denied. \"Annual refresh\" is an answer; a blank is not.",
    ar: "مطلوب. هذا ما سيقرأه شخص بعد ثلاث سنوات ليعرف لماذا تغيّر هذا الرمز في الأسبوع الذي رُفضت فيه "
      + "مطالبة. \"التحديث السنوي\" إجابة، أما الفراغ فلا.",
  },
  rationaleMissing: { en: "State why. It is recorded with the version.", ar: "اذكر السبب. يُسجَّل مع الإصدار." },
  codeMissing: { en: "A code is required.", ar: "الرمز مطلوب." },
  retireLabel: { en: "Retire this code", ar: "إيقاف هذا الرمز" },
  retireHint: {
    en: "Recorded as a new version that marks the code retired. Nothing is deleted and history still resolves.",
    ar: "يُسجَّل كإصدار جديد يُعلِّم الرمز موقوفاً. لا يُحذف شيء ويظل السجل التاريخي قابلاً للقراءة.",
  },

  // ---- the diff ----
  diff: { en: "What changes", ar: "ما الذي يتغيّر" },
  diffHint: {
    en: "The version in force, beside what you are about to write. A code table edited blind is how a wrong "
      + "mapping ships.",
    ar: "الإصدار الساري بجانب ما أنت على وشك كتابته. تعديل جدول الرموز دون مراجعة هو ما يُدخل تعييناً خاطئاً.",
  },
  attribute: { en: "Attribute", ar: "الخاصية" },
  before: { en: "Now", ar: "الآن" },
  after: { en: "After", ar: "بعد" },
  added: { en: "Added", ar: "مضاف" },
  removed: { en: "Removed", ar: "محذوف" },
  changed: { en: "Changed", ar: "متغيّر" },
  unchanged: { en: "Unchanged", ar: "دون تغيير" },
  noChange: {
    en: "Nothing changes. Saving would append a version identical to the one in force.",
    ar: "لا شيء يتغيّر. الحفظ سيضيف إصداراً مطابقاً للإصدار الساري.",
  },
  diffUnavailable: {
    en: "The version in force could not be read, so this change cannot be compared against it. You can still "
      + "save — but you are writing without seeing what you are replacing.",
    ar: "تعذّرت قراءة الإصدار الساري، لذا لا يمكن مقارنة هذا التغيير به. يمكنك الحفظ — لكنك تكتب دون أن ترى "
      + "ما تستبدله.",
  },
  loadingDiff: { en: "Reading the version in force…", ar: "جارٍ قراءة الإصدار الساري…" },

  // ---- outcomes ----
  save: { en: "Save new version", ar: "حفظ إصدار جديد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  saved: { en: "Saved as version {n}.", ar: "حُفظ كإصدار {n}." },
  failed: { en: "Could not save.", ar: "تعذّر الحفظ." },
  outOfScope: {
    en: "That code system is not part of clinical governance. ICD-10, CPT, LOINC and ATC are yours; the rest "
      + "belongs to the platform administrators.",
    ar: "نظام الرموز هذا ليس ضمن الحوكمة السريرية. ICD-10 وCPT وLOINC وATC من صلاحياتك، والباقي لمديري "
      + "المنصّة.",
  },
} satisfies Record<string, Localized>;

/** A `name = value` line, forgiving about spacing and about `=` appearing in the value. */
function parseAttributes(text: string): { attrs: Record<string, string>; bad: number[] } {
  const attrs: Record<string, string> = {};
  const bad: number[] = [];
  text.split("\n").forEach((raw, i) => {
    const line = raw.trim();
    if (line === "") return;
    const at = line.indexOf("=");
    if (at <= 0) { bad.push(i + 1); return; }
    attrs[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  });
  return { attrs, bad };
}

const asText = (v: unknown) =>
  v === null || v === undefined ? "" : typeof v === "object" ? JSON.stringify(v) : String(v);

/**
 * The clinical master lists, and the editor over them (ADR-0035 §4).
 *
 * <b>Why this screen is on the supervisor's portal.</b> `medical_director` already held
 * `admin:edit-masterdata`, and `POST /api/v1/admin/master-data` was already built — effective-dated,
 * versioned, rationale-mandatory, audited. What was missing was a door: `portalForRole` gives one portal per
 * role, the only Master Data screen lived in the `admin` portal, and it was read-only anyway. The authority
 * had been granted and then had nowhere to be used.
 *
 * <b>Why an edit is an append.</b> Codes are safety-critical and historical records must keep resolving them
 * as they read at the time — a claim adjudicated last March is judged against last March's ICD entry. So the
 * prior version's window closes and a new one opens; nothing is mutated and nothing is deleted. Retiring a
 * code is itself a version.
 *
 * <b>Why the diff is not optional.</b> A code table edited blind is how a wrong mapping ships, and a wrong
 * mapping does not fail loudly — it misroutes a diagnosis and surfaces weeks later as a decision that looks
 * wrong for no visible reason. If the version in force cannot be read, the screen SAYS the change cannot be
 * compared rather than showing an empty diff that reads as "nothing changes".
 */
export function MasterListAdmin() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const { lang } = useTheme();
  const { toast } = useToast();
  const write = useWrite();

  const state = useAsync<MasterDataVersion[]>(() => api.adminMasterData(), []);
  const [editing, setEditing] = useState<{ system: ClinicalCodeSystem; code: string } | null>(null);

  const cols: Column<MasterDataVersion>[] = [
    { key: "system", header: t(S.system), sortable: true, cell: (r) => <span className="tnum">{r.system}</span>, sortValue: (r) => r.system },
    { key: "code", header: t(S.code), sortable: true, cell: (r) => <span className="tnum">{r.code}</span>, sortValue: (r) => r.code },
    { key: "version", header: t(S.version), cell: (r) => `v${r.versionNo}`, numeric: true },
    {
      key: "status", header: t(S.status),
      // Word + hue, never hue alone.
      cell: (r) => <StatusChip kind={r.retired ? "warn" : "ok"} label={t(r.retired ? S.retired : S.active)} />,
    },
    { key: "effective", header: t(S.effective), sortable: true, cell: (r) => <span className="tnum">{fmt.date(r.effectiveFrom)}</span>, sortValue: (r) => r.effectiveFrom },
    { key: "rationale", header: t(S.rationale), cell: (r) => r.rationale ?? <span className="muted">—</span> },
    {
      key: "edit", header: t(S.edit), stickyEnd: true,
      cell: (r) => {
        // Only the clinical systems are editable here; the rest are shown (they are in force and the
        // supervisor should see them) but carry no control, rather than a control that 403s.
        const clinical = (CLINICAL_CODE_SYSTEMS as readonly string[]).includes(r.system);
        if (!clinical) return <span className="muted">—</span>;
        return (
          <Button
            variant="secondary" size="sm"
            leadingIcon={<Icon name="pen" />}
            // Named, not a row of identical "Edit" buttons — unusable by keyboard or screen reader, and the
            // wrong row edits the wrong code.
            aria-label={t(S.editNamed).replace("{code}", `${r.system} ${r.code}`)}
            onClick={() => setEditing({ system: r.system as ClinicalCodeSystem, code: r.code })}
          >
            {t(S.edit)}
          </Button>
        );
      },
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />

      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <p className="muted">{t(S.lede)}</p>
        <InlineAlert tone="info">{t(S.appendOnly)}</InlineAlert>
        <p className="muted" style={{ marginBlockStart: "var(--sp3)" }}>{t(S.scope)}</p>
      </Card>

      <Card as="section" style={{ padding: "var(--sp3)", marginBlockStart: "var(--sp4)" }}>
        <div className="rx-card-head">
          <h2 className="section-h">{t(S.inForce)}</h2>
          <Button
            variant="primary" size="sm" leadingIcon={<Icon name="plus" />}
            onClick={() => setEditing({ system: "Icd10", code: "" })}
          >
            {t(S.newVersion)}
          </Button>
        </div>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.inForce)} />}
        </AsyncSection>
      </Card>

      {editing && (
        <MasterListEditor
          key={`${editing.system}:${editing.code}`}
          system={editing.system}
          code={editing.code}
          onCancel={() => setEditing(null)}
          onSaved={async (versionNo) => {
            toast(t(S.saved).replace("{n}", String(versionNo)), "ok");
            setEditing(null);
            await state.reload();
          }}
          onFailed={(e) => {
            const problem = writeErrorMessage(e);
            toast(
              problem.problemType === "urn:hbmp:code-system-out-of-scope"
                ? t(S.outOfScope)
                : writeErrorText(problem, lang) ?? t(S.failed),
              "bad",
            );
          }}
          write={write}
          api={api}
          t={t}
        />
      )}
    </>
  );
}

/**
 * One proposed version, with its diff.
 *
 * Kept as its own component and re-mounted per code (`key`) so switching rows can never leave the previous
 * code's attributes in the box — an editor that carried them over would write one code's meaning onto another.
 */
function MasterListEditor({
  system, code: initialCode, onCancel, onSaved, onFailed, write, api, t,
}: {
  system: ClinicalCodeSystem;
  code: string;
  onCancel: () => void;
  onSaved: (versionNo: number) => void | Promise<void>;
  onFailed: (e: unknown) => void;
  write: ReturnType<typeof useWrite>;
  api: ReturnType<typeof useApi>;
  t: (l: Localized) => string;
}) {
  const [sys, setSys] = useState<ClinicalCodeSystem>(system);
  const [code, setCode] = useState(initialCode);
  const [attrText, setAttrText] = useState("");
  const [rationale, setRationale] = useState("");
  const [retired, setRetired] = useState(false);
  const [busy, setBusy] = useState(false);
  const [touched, setTouched] = useState(false);

  // The version in force, for the diff. Only fetched for an existing code — a brand new one has nothing to
  // compare against, and asking would 404 and read as a failure.
  const current = useAsync<MasterDataAsOf | null>(
    async () => (initialCode ? await api.adminMasterDataAsOf(system, initialCode, new Date().toISOString()) : null),
    [system, initialCode],
  );

  const parsed = useMemo(() => parseAttributes(attrText), [attrText]);
  const codeMissing = touched && code.trim() === "";
  const rationaleMissing = touched && rationale.trim() === "";
  const invalid = codeMissing || rationaleMissing || parsed.bad.length > 0;

  // Every attribute mentioned by either side, so a REMOVED one is as visible as an added one. Diffing only
  // the proposed keys would hide the deletion, which is the change most likely to break a downstream read.
  const rows = useMemo(() => {
    const before = (current.data?.attributes ?? {}) as Record<string, unknown>;
    const after = parsed.attrs;
    const keys = [...new Set([...Object.keys(before), ...Object.keys(after)])].sort();
    return keys.map((k) => {
      const b = asText(before[k]);
      const a = Object.prototype.hasOwnProperty.call(after, k) ? after[k] : undefined;
      const aText = a === undefined ? "" : asText(a);
      const kind =
        !(k in before) ? "added" : a === undefined ? "removed" : b === aText ? "unchanged" : "changed";
      return { key: k, before: b, after: aText, kind };
    });
  }, [current.data, parsed.attrs]);

  const moved = rows.filter((r) => r.kind !== "unchanged");
  const chip = { added: "ok", removed: "bad", changed: "warn", unchanged: "neu" } as const;
  const label = { added: S.added, removed: S.removed, changed: S.changed, unchanged: S.unchanged } as const;

  async function save() {
    setTouched(true);
    if (code.trim() === "" || rationale.trim() === "" || parsed.bad.length > 0) return;
    setBusy(true);
    try {
      const r = await api.adminMasterDataUpsert({
        system: sys, code: code.trim(), attributes: parsed.attrs, rationale: rationale.trim(), retired,
      });
      await onSaved(r.versionNo);
    } catch (e) {
      onFailed(e);
    } finally {
      setBusy(false);
      void write;
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", marginBlockStart: "var(--sp4)" }}>
      <h2 className="section-h">{t(S.newVersion)}</h2>

      <div className="stack" style={{ marginBlockStart: "var(--sp3)" }}>
        <SelectField
          label={t(S.system)}
          value={sys}
          onChange={(v) => setSys(v as ClinicalCodeSystem)}
          options={CLINICAL_CODE_SYSTEMS.map((s) => ({ value: s, label: s }))}
        />
        <InputField
          label={t(S.code)}
          value={code}
          error={codeMissing ? t(S.codeMissing) : undefined}
          onChange={(e) => setCode(e.currentTarget.value)}
        />
        <TextareaField
          label={t(S.attributes)}
          help={t(S.attributesHint)}
          rows={5}
          value={attrText}
          error={parsed.bad.length > 0 ? t(S.attributesInvalid).replace("{lines}", parsed.bad.join(", ")) : undefined}
          onChange={(e) => setAttrText(e.currentTarget.value)}
        />
        <TextareaField
          label={t(S.rationaleLabel)}
          help={t(S.rationaleHint)}
          rows={2}
          value={rationale}
          error={rationaleMissing ? t(S.rationaleMissing) : undefined}
          onChange={(e) => setRationale(e.currentTarget.value)}
        />
        <label className="md-retire">
          <input type="checkbox" checked={retired} onChange={(e) => setRetired(e.currentTarget.checked)} />
          <span>
            <strong>{t(S.retireLabel)}</strong>
            <span className="muted"> — {t(S.retireHint)}</span>
          </span>
        </label>
      </div>

      <h3 className="section-h" style={{ marginBlockStart: "var(--sp5)" }}>{t(S.diff)}</h3>
      <p className="muted">{t(S.diffHint)}</p>

      {current.status === "loading" && <p className="muted">{t(S.loadingDiff)}</p>}
      {/* A failed read is NEVER an empty diff. An empty diff reads as "nothing changes", which is the one
          thing it must not say when the truth is "we could not see what is there". */}
      {current.status === "error" && <InlineAlert tone="warn">{t(S.diffUnavailable)}</InlineAlert>}
      {current.status === "success" && moved.length === 0 && rows.length > 0 && (
        <InlineAlert tone="info">{t(S.noChange)}</InlineAlert>
      )}
      {current.status === "success" && rows.length > 0 && (
        <table className="mini-table md-diff">
          <caption className="muted mini-table-cap">{t(S.diff)}</caption>
          <thead>
            <tr>
              <th scope="col">{t(S.attribute)}</th>
              <th scope="col">{t(S.before)}</th>
              <th scope="col">{t(S.after)}</th>
              <th scope="col">{t(S.status)}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.key}>
                <td><span className="mono">{r.key}</span></td>
                <td>{r.before || <span className="muted">—</span>}</td>
                <td>{r.after || <span className="muted">—</span>}</td>
                <td><StatusChip kind={chip[r.kind as keyof typeof chip]} label={t(label[r.kind as keyof typeof label])} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="pol-editor-actions">
        <Button variant="ghost" onClick={onCancel}>{t(S.cancel)}</Button>
        <Button
          variant="primary"
          loading={busy}
          disabled={invalid}
          leadingIcon={<Icon name="check2" />}
          onClick={() => void save()}
        >
          {t(S.save)}
        </Button>
      </div>
    </Card>
  );
}
