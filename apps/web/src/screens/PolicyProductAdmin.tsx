import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, Card, DataTable, InlineAlert, StatusChip } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  ActivationProblem,
  BenefitCategoryView,
  BenefitRuleInput,
  BenefitRuleTierInput,
  BenefitRuleView,
  NetworkTierView,
  PayerView,
  PlanVersionView,
  PlanView,
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

/**
 * Phase 19.6 — payers, plans, and THE PLAN VERSION EDITOR (design 38 §4.1 / §4.1b).
 *
 * The editor is the screen this whole portal exists for, and its central claim is structural: a Draft is
 * freely editable and an Active version is immutable, so the UI must not offer an edit it will be refused.
 * `editable` is projected by the service rather than derived here from the status — deriving the same rule in
 * two places is how a read-only screen ends up with a working save button.
 *
 * The grid is TWO LEVELS because a benefit has two kinds of fact. What the plan covers (limit, reset,
 * waiting period, pre-authorisation, exclusions) is one row per benefit category. What the MEMBER PAYS
 * depends additionally on where they were treated — so each category expands into a cost-share matrix with
 * one column per Active network tier. Flattening those into a single grid would ask an administrator to hold
 * "category × tier" in their head while typing; keeping them separate makes an unpriced tier visible as an
 * empty cell, which is exactly what activation refuses.
 */

const S = {
  payers: { en: "Payers", ar: "الجهات الممولة" },
  plans: { en: "Plans & Versions", ar: "الخطط والإصدارات" },
  payerCode: { en: "Code", ar: "الرمز" },
  name: { en: "Name", ar: "الاسم" },
  type: { en: "Type", ar: "النوع" },
  status: { en: "Status", ar: "الحالة" },
  category: { en: "Category", ar: "الفئة" },
  noPayers: { en: "No payers configured.", ar: "لا توجد جهات ممولة." },
  noPlans: { en: "No plans configured.", ar: "لا توجد خطط." },
  versions: { en: "Versions", ar: "الإصدارات" },
  version: { en: "Version", ar: "الإصدار" },
  window: { en: "In force", ar: "سارٍ" },
  draft: { en: "Draft — editable", ar: "مسودة — قابلة للتعديل" },
  immutable: { en: "Active — immutable", ar: "سارٍ — غير قابل للتعديل" },
  immutableHint: {
    en: "An active version is the benefit configuration claims are judged against, so it can never be edited. Amend it to create a new draft.",
    ar: "الإصدار الساري هو تكوين المنافع الذي تُقيَّم عليه المطالبات، ولذلك لا يمكن تعديله. عدّله لإنشاء مسودة جديدة.",
  },
  amend: { en: "Amend — create a new draft", ar: "تعديل — إنشاء مسودة جديدة" },
  validate: { en: "Validate", ar: "التحقق" },
  activate: { en: "Activate", ar: "التفعيل" },
  saveRules: { en: "Save benefit configuration", ar: "حفظ تكوين المنافع" },
  valid: { en: "This draft passes validation and can be activated.", ar: "اجتازت هذه المسودة التحقق ويمكن تفعيلها." },
  covered: { en: "Covered", ar: "مغطّى" },
  limit: { en: "Limit", ar: "الحد" },
  limitType: { en: "Limit type", ar: "نوع الحد" },
  reset: { en: "Reset", ar: "إعادة التعيين" },
  waiting: { en: "Waiting (days)", ar: "فترة الانتظار (أيام)" },
  preauth: { en: "Pre-auth", ar: "موافقة مسبقة" },
  exclusions: { en: "Exclusions", ar: "الاستثناءات" },
  costShare: { en: "Cost share by network tier", ar: "مشاركة التكلفة حسب شريحة الشبكة" },
  copay: { en: "Co-pay", ar: "المشاركة الثابتة" },
  coinsurance: { en: "Co-insurance %", ar: "نسبة المشاركة ٪" },
  expand: { en: "Cost share", ar: "مشاركة التكلفة" },
  noTiers: {
    en: "No active network tiers. Cost share cannot be authored until the Network Team defines one.",
    ar: "لا توجد شرائح شبكة نشطة. لا يمكن تحديد مشاركة التكلفة حتى تنشئ الشبكة شريحة.",
  },
  unpriced: { en: "Unpriced", ar: "غير مسعّر" },
  unpricedHint: {
    en: "A covered category that leaves an active tier unpriced cannot be activated.",
    ar: "لا يمكن تفعيل فئة مغطّاة تترك شريحة نشطة دون تسعير.",
  },
  diff: { en: "What changed from the previous version", ar: "ما تغيّر عن الإصدار السابق" },
  noDiff: { en: "No previous version to compare against.", ar: "لا يوجد إصدار سابق للمقارنة." },
  unchanged: { en: "No differences in benefit configuration.", ar: "لا توجد فروق في تكوين المنافع." },
  added: { en: "Added", ar: "مضاف" },
  removed: { en: "Removed", ar: "محذوف" },
  changed: { en: "Changed", ar: "معدّل" },
  saved: { en: "Benefit configuration saved.", ar: "تم حفظ تكوين المنافع." },
  activated: { en: "Version activated. It is now immutable.", ar: "تم تفعيل الإصدار. أصبح غير قابل للتعديل." },
  amended: { en: "New draft created from the active version.", ar: "تم إنشاء مسودة جديدة من الإصدار الساري." },
  selectPlan: { en: "Select a plan to see its versions.", ar: "اختر خطة لعرض إصداراتها." },
  members: { en: "Members", ar: "الأعضاء" },
} satisfies Record<string, Localized>;

const LIMIT_TYPES = ["", "Annual", "PerEncounter", "Lifetime", "Count"];
const RESET_PERIODS = ["None", "Monthly", "Quarterly", "Yearly"];

// ── Payers ──────────────────────────────────────────────────────────────────────────────────────────────

export function PolicyPayers({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const [rows, setRows] = useState<PayerView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    api.payers().then((r) => live && setRows(r)).catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.payers)} />
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <Card>
        <DataTable
          caption={t(S.payers)}
          rows={rows ?? []}
          rowKey={(r) => r.payerId}
          loading={rows === null && !error}
          emptyLabel={t(S.noPayers)}
          columns={[
            { key: "code", header: t(S.payerCode), cell: (r) => r.payerCode },
            { key: "name", header: t(S.name), cell: (r) => <BiName en={r.nameEn} ar={r.nameAr} /> },
            { key: "type", header: t(S.type), cell: (r) => r.payerType },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
          ]}
        />
      </Card>
    </div>
  );
}

function BiName({ en, ar }: { en: string; ar: string }) {
  const t = useLoc();
  return <>{t({ en, ar })}</>;
}

// ── Plans + the version editor ──────────────────────────────────────────────────────────────────────────

export function PolicyPlans({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const [plans, setPlans] = useState<PlanView[] | null>(null);
  const [categories, setCategories] = useState<BenefitCategoryView[]>([]);
  const [tiers, setTiers] = useState<NetworkTierView[]>([]);
  const [selectedPlan, setSelectedPlan] = useState<string | null>(null);
  const [versions, setVersions] = useState<PlanVersionView[]>([]);
  const [selectedVersion, setSelectedVersion] = useState<string | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");

  useEffect(() => {
    let live = true;
    Promise.all([api.plans(), api.benefitCategories(), api.networkTiers()])
      .then(([p, c, tr]) => {
        if (!live) return;
        setPlans(p);
        setCategories(c);
        setTiers(tr);
      })
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  const loadVersions = useCallback(
    async (planId: string) => {
      try {
        const v = await api.planVersions(planId);
        setVersions(v);
        setSelectedVersion(v[0]?.planVersionId ?? null);
      } catch (e) {
        setError(readErrorMessage(e));
      }
    },
    [api],
  );

  useEffect(() => {
    if (selectedPlan) void loadVersions(selectedPlan);
  }, [selectedPlan, loadVersions]);

  /** Only ACTIVE tiers get a column: pricing against a retired tier authors a number nothing will ever read. */
  const activeTiers = useMemo(
    () => tiers.filter((x) => x.status === "Active").sort((a, b) => a.rank - b.rank),
    [tiers],
  );

  const version = versions.find((v) => v.planVersionId === selectedVersion) ?? null;
  const previous = version
    ? versions.filter((v) => v.versionNo < version.versionNo).sort((a, b) => b.versionNo - a.versionNo)[0] ?? null
    : null;

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.plans)} />
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <Card>
        <DataTable
          caption={t(S.plans)}
          rows={plans ?? []}
          rowKey={(r) => r.planId}
          interactive
          selectedKey={selectedPlan}
          onSelect={(r) => setSelectedPlan(r.planId)}
          loading={plans === null && !error}
          emptyLabel={t(S.noPlans)}
          columns={[
            { key: "code", header: t(S.payerCode), cell: (r) => r.planCode },
            { key: "name", header: t(S.name), cell: (r) => <BiName en={r.nameEn} ar={r.nameAr} /> },
            { key: "category", header: t(S.category), cell: (r) => r.category },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
          ]}
        />
      </Card>

      {!selectedPlan && <InlineAlert tone="info">{t(S.selectPlan)}</InlineAlert>}

      {selectedPlan && (
        <Card>
          <h3>{t(S.versions)}</h3>
          {/* The version timeline. Superseded versions stay listed and resolvable forever, because a claim
              for care given last March must still be judged by March's rules. */}
          <ol className="pol-versions">
            {versions.map((v) => (
              <li key={v.planVersionId}>
                <Button
                  variant={v.planVersionId === selectedVersion ? "primary" : "ghost"}
                  onClick={() => setSelectedVersion(v.planVersionId)}
                >
                  {t(S.version)} {v.versionNo}
                </Button>
                <StatusChip
                  kind={v.status === "Active" ? "ok" : v.status === "Draft" ? "info" : "neu"}
                  label={v.status}
                />
                <span>
                  {t(S.window)}: {fmt.date(v.effectiveFrom)} → {v.effectiveTo ? fmt.date(v.effectiveTo) : "—"}
                </span>
              </li>
            ))}
          </ol>
          <Button
            variant="secondary"
            onClick={async () => {
              try {
                await api.amendPlan(selectedPlan, crypto.randomUUID());
                setAnnounce(t(S.amended));
                await loadVersions(selectedPlan);
              } catch (e) {
                setError(writeErrorMessage(e).message);
              }
            }}
          >
            {t(S.amend)}
          </Button>
        </Card>
      )}

      {version && (
        <PlanVersionEditor
          api={api}
          version={version}
          previous={previous}
          categories={categories}
          tiers={activeTiers}
          onChanged={async () => {
            if (selectedPlan) await loadVersions(selectedPlan);
          }}
          onAnnounce={setAnnounce}
        />
      )}
    </div>
  );
}

// ── The two-level grid ──────────────────────────────────────────────────────────────────────────────────

interface EditableTier extends BenefitRuleTierInput {
  tierCode: string;
}

interface EditableRule {
  benefitCategoryCode: string;
  isCovered: boolean;
  limitType: string;
  limitValue: string;
  resetPeriod: string;
  waitingPeriodDays: string;
  requiresPreauth: boolean;
  exclusions: string;
  deductible: string;
  deductibleWaived: boolean;
  preauthCostThreshold: string;
  tiers: EditableTier[];
}

function toEditable(
  rules: BenefitRuleView[],
  categories: BenefitCategoryView[],
  tiers: NetworkTierView[],
): EditableRule[] {
  const byCode = new Map(
    rules
      .filter((r) => r.benefitCategoryCode)
      .map((r) => [r.benefitCategoryCode as string, r]),
  );
  // One row per CATEGORY, not per configured rule: the category nobody has priced yet is precisely the row
  // an administrator opens the editor to fill in, and it cannot appear if the rows come from the rule set.
  return categories.map((c) => {
    const r = byCode.get(c.code);
    return {
      benefitCategoryCode: c.code,
      isCovered: r?.isCovered ?? false,
      limitType: r?.limitType ?? "",
      limitValue: r?.limitValue != null ? String(r.limitValue) : "",
      resetPeriod: r?.resetPeriod ?? "None",
      waitingPeriodDays: String(r?.waitingPeriodDays ?? 0),
      requiresPreauth: r?.requiresPreauth ?? false,
      exclusions: r?.exclusions ?? "[]",
      deductible: r?.deductible != null ? String(r.deductible) : "",
      deductibleWaived: r?.deductibleWaived ?? false,
      preauthCostThreshold: r?.preauthCostThreshold != null ? String(r.preauthCostThreshold) : "",
      tiers: tiers.map((tier) => {
        const existing = r?.tiers.find((x) => x.networkTierId === tier.networkTierId);
        return {
          tierCode: tier.tierCode,
          networkTierId: tier.networkTierId,
          isCovered: existing?.isCovered ?? false,
          copayFixed: existing?.copayFixed ?? null,
          copayPercent: existing?.copayPercent ?? null,
          coinsurancePercent: existing?.coinsurancePercent ?? null,
          copayCountsTowardDeductible: existing?.copayCountsTowardDeductible ?? false,
          requiresPreauthOverride: existing?.requiresPreauthOverride ?? null,
          limitMultiplier: existing?.limitMultiplier ?? null,
        };
      }),
    };
  });
}

const num = (s: string): number | null => (s.trim() === "" ? null : Number(s));

function toInput(rules: EditableRule[]): BenefitRuleInput[] {
  return rules.map((r) => ({
    benefitCategoryCode: r.benefitCategoryCode,
    isCovered: r.isCovered,
    limitType: r.limitType || null,
    limitValue: num(r.limitValue),
    resetPeriod: r.resetPeriod,
    deductible: num(r.deductible),
    deductibleWaived: r.deductibleWaived,
    waitingPeriodDays: Number(r.waitingPeriodDays || "0"),
    requiresPreauth: r.requiresPreauth,
    preauthCostThreshold: num(r.preauthCostThreshold),
    exclusions: r.exclusions,
    notes: null,
    tiers: r.tiers.map(({ tierCode: _tierCode, ...rest }) => rest),
  }));
}

function PlanVersionEditor({
  api,
  version,
  previous,
  categories,
  tiers,
  onChanged,
  onAnnounce,
}: {
  api: PolicyApi;
  version: PlanVersionView;
  previous: PlanVersionView | null;
  categories: BenefitCategoryView[];
  tiers: NetworkTierView[];
  onChanged: () => Promise<void>;
  onAnnounce: (s: string) => void;
}) {
  const t = useLoc();
  const [rules, setRules] = useState<EditableRule[]>(() => toEditable(version.rules, categories, tiers));
  const [expanded, setExpanded] = useState<string | null>(null);
  const [problems, setProblems] = useState<ActivationProblem[] | null>(null);
  const [valid, setValid] = useState(false);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [saveKey, rotateSaveKey] = useIdempotencyKey();
  const [activateKey, rotateActivateKey] = useIdempotencyKey();

  useEffect(() => {
    setRules(toEditable(version.rules, categories, tiers));
    setProblems(null);
    setValid(false);
  }, [version, categories, tiers]);

  const editable = version.editable;

  function patch(code: string, change: Partial<EditableRule>) {
    setRules((prev) => prev.map((r) => (r.benefitCategoryCode === code ? { ...r, ...change } : r)));
  }
  function patchTier(code: string, tierId: string, change: Partial<EditableTier>) {
    setRules((prev) =>
      prev.map((r) =>
        r.benefitCategoryCode === code
          ? { ...r, tiers: r.tiers.map((x) => (x.networkTierId === tierId ? { ...x, ...change } : x)) }
          : r,
      ),
    );
  }

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await api.setPlanRules(version.planVersionId, toInput(rules), saveKey);
      rotateSaveKey();
      onAnnounce(t(S.saved));
      await onChanged();
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function validate() {
    setBusy(true);
    setError(null);
    try {
      const r = await api.validatePlanVersion(version.planVersionId);
      setProblems(r.problems);
      setValid(r.valid);
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function activate() {
    setBusy(true);
    setError(null);
    try {
      await api.activatePlanVersion(version.planVersionId, activateKey);
      rotateActivateKey();
      onAnnounce(t(S.activated));
      await onChanged();
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card data-testid="plan-version-editor">
      <div className="pol-editor-head">
        <h3>
          {t(S.version)} {version.versionNo}
        </h3>
        {editable ? (
          <StatusChip kind="info" label={t(S.draft)} />
        ) : (
          <StatusChip kind="neu" label={t(S.immutable)} />
        )}
      </div>

      {!editable && (
        // The explicit "immutable — amend to change" affordance the acceptance criterion asks for. It says
        // WHY, and it offers the only legitimate way forward rather than leaving a disabled form.
        <InlineAlert tone="info" data-testid="immutable-notice">
          {t(S.immutableHint)}
        </InlineAlert>
      )}

      {tiers.length === 0 && <InlineAlert tone="warn">{t(S.noTiers)}</InlineAlert>}
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <table className="pol-grid">
        <caption className="sr-only">{t(S.plans)}</caption>
        <thead>
          <tr>
            <th scope="col">{t(S.category)}</th>
            <th scope="col">{t(S.covered)}</th>
            <th scope="col">{t(S.limitType)}</th>
            <th scope="col">{t(S.limit)}</th>
            <th scope="col">{t(S.reset)}</th>
            <th scope="col">{t(S.waiting)}</th>
            <th scope="col">{t(S.preauth)}</th>
            <th scope="col">{t(S.exclusions)}</th>
            <th scope="col">{t(S.expand)}</th>
          </tr>
        </thead>
        <tbody>
          {rules.map((r) => {
            const unpriced = r.isCovered && r.tiers.some((x) => !x.isCovered && x.copayFixed == null && x.coinsurancePercent == null);
            const open = expanded === r.benefitCategoryCode;
            return [
              <tr key={r.benefitCategoryCode}>
                <th scope="row">
                  {r.benefitCategoryCode}
                  {unpriced && <StatusChip kind="warn" label={t(S.unpriced)} />}
                </th>
                <td>
                  <input
                    type="checkbox"
                    aria-label={`${t(S.covered)} — ${r.benefitCategoryCode}`}
                    checked={r.isCovered}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { isCovered: e.target.checked })}
                  />
                </td>
                <td>
                  <select
                    aria-label={`${t(S.limitType)} — ${r.benefitCategoryCode}`}
                    value={r.limitType}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { limitType: e.target.value })}
                  >
                    {LIMIT_TYPES.map((x) => (
                      <option key={x || "none"} value={x}>
                        {x || "—"}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    inputMode="decimal"
                    aria-label={`${t(S.limit)} — ${r.benefitCategoryCode}`}
                    value={r.limitValue}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { limitValue: e.target.value })}
                  />
                </td>
                <td>
                  <select
                    aria-label={`${t(S.reset)} — ${r.benefitCategoryCode}`}
                    value={r.resetPeriod}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { resetPeriod: e.target.value })}
                  >
                    {RESET_PERIODS.map((x) => (
                      <option key={x} value={x}>
                        {x}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input
                    inputMode="numeric"
                    aria-label={`${t(S.waiting)} — ${r.benefitCategoryCode}`}
                    value={r.waitingPeriodDays}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { waitingPeriodDays: e.target.value })}
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    aria-label={`${t(S.preauth)} — ${r.benefitCategoryCode}`}
                    checked={r.requiresPreauth}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { requiresPreauth: e.target.checked })}
                  />
                </td>
                <td>
                  <input
                    aria-label={`${t(S.exclusions)} — ${r.benefitCategoryCode}`}
                    value={r.exclusions}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { exclusions: e.target.value })}
                  />
                </td>
                <td>
                  <Button
                    variant="ghost"
                    aria-expanded={open}
                    onClick={() => setExpanded(open ? null : r.benefitCategoryCode)}
                  >
                    {t(S.expand)}
                  </Button>
                </td>
              </tr>,
              open ? (
                <tr key={`${r.benefitCategoryCode}-tiers`} className="pol-grid-sub">
                  <td colSpan={9}>
                    <fieldset>
                      <legend>
                        {t(S.costShare)} — {r.benefitCategoryCode}
                      </legend>
                      {unpriced && <InlineAlert tone="warn">{t(S.unpricedHint)}</InlineAlert>}
                      <table className="pol-costshare">
                        <thead>
                          <tr>
                            <th scope="col">{t(S.type)}</th>
                            {r.tiers.map((x) => (
                              <th key={x.networkTierId} scope="col">
                                {x.tierCode}
                              </th>
                            ))}
                          </tr>
                        </thead>
                        <tbody>
                          <tr>
                            <th scope="row">{t(S.covered)}</th>
                            {r.tiers.map((x) => (
                              <td key={x.networkTierId}>
                                <input
                                  type="checkbox"
                                  aria-label={`${t(S.covered)} — ${r.benefitCategoryCode} — ${x.tierCode}`}
                                  checked={x.isCovered}
                                  disabled={!editable}
                                  onChange={(e) =>
                                    patchTier(r.benefitCategoryCode, x.networkTierId, { isCovered: e.target.checked })
                                  }
                                />
                              </td>
                            ))}
                          </tr>
                          <tr>
                            <th scope="row">{t(S.copay)}</th>
                            {r.tiers.map((x) => (
                              <td key={x.networkTierId}>
                                <input
                                  inputMode="decimal"
                                  aria-label={`${t(S.copay)} — ${r.benefitCategoryCode} — ${x.tierCode}`}
                                  value={x.copayFixed ?? ""}
                                  disabled={!editable}
                                  onChange={(e) =>
                                    patchTier(r.benefitCategoryCode, x.networkTierId, {
                                      copayFixed: e.target.value === "" ? null : Number(e.target.value),
                                    })
                                  }
                                />
                              </td>
                            ))}
                          </tr>
                          <tr>
                            <th scope="row">{t(S.coinsurance)}</th>
                            {r.tiers.map((x) => (
                              <td key={x.networkTierId}>
                                <input
                                  inputMode="decimal"
                                  aria-label={`${t(S.coinsurance)} — ${r.benefitCategoryCode} — ${x.tierCode}`}
                                  value={x.coinsurancePercent ?? ""}
                                  disabled={!editable}
                                  onChange={(e) =>
                                    patchTier(r.benefitCategoryCode, x.networkTierId, {
                                      coinsurancePercent: e.target.value === "" ? null : Number(e.target.value),
                                    })
                                  }
                                />
                              </td>
                            ))}
                          </tr>
                        </tbody>
                      </table>
                    </fieldset>
                  </td>
                </tr>
              ) : null,
            ];
          })}
        </tbody>
      </table>

      <div className="pol-editor-actions">
        {editable && (
          <Button variant="primary" onClick={save} disabled={busy}>
            {t(S.saveRules)}
          </Button>
        )}
        <Button variant="secondary" onClick={validate} disabled={busy}>
          {t(S.validate)}
        </Button>
        {editable && (
          <Button variant="primary" onClick={activate} disabled={busy || !valid}>
            {t(S.activate)}
          </Button>
        )}
      </div>

      {problems !== null && (
        <div aria-live="polite" data-testid="validation-result">
          {valid ? (
            <InlineAlert tone="ok">{t(S.valid)}</InlineAlert>
          ) : (
            <ul className="pol-problems">
              {problems.map((p) => (
                <li key={`${p.code}-${p.detail}`}>
                  <StatusChip kind="bad" label={p.code} /> {p.detail}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      <VersionDiff current={version} previous={previous} />
    </Card>
  );
}

// ── Version diff ────────────────────────────────────────────────────────────────────────────────────────

interface DiffLine {
  category: string;
  kind: "added" | "removed" | "changed";
  detail: string;
}

/** Compare two rule sets field by field. Rendered rather than left to the reader because "what did this
 *  amendment actually change" is the question every plan amendment is reviewed on, and diffing two screens
 *  by eye is how a changed waiting period ships unnoticed. */
export function diffRules(current: BenefitRuleView[], previous: BenefitRuleView[]): DiffLine[] {
  const key = (r: BenefitRuleView) => r.benefitCategoryCode ?? r.benefitCategoryId;
  const prev = new Map(previous.map((r) => [key(r), r]));
  const lines: DiffLine[] = [];

  for (const r of current) {
    const k = key(r);
    const p = prev.get(k);
    if (!p) {
      lines.push({ category: k, kind: "added", detail: r.isCovered ? "covered" : "not covered" });
      continue;
    }
    prev.delete(k);
    const changes: string[] = [];
    if (p.isCovered !== r.isCovered) changes.push(`covered ${p.isCovered} → ${r.isCovered}`);
    if (p.limitValue !== r.limitValue) changes.push(`limit ${p.limitValue ?? "—"} → ${r.limitValue ?? "—"}`);
    if (p.limitType !== r.limitType) changes.push(`limit type ${p.limitType ?? "—"} → ${r.limitType ?? "—"}`);
    if (p.resetPeriod !== r.resetPeriod) changes.push(`reset ${p.resetPeriod} → ${r.resetPeriod}`);
    if (p.waitingPeriodDays !== r.waitingPeriodDays)
      changes.push(`waiting ${p.waitingPeriodDays}d → ${r.waitingPeriodDays}d`);
    if (p.requiresPreauth !== r.requiresPreauth)
      changes.push(`pre-auth ${p.requiresPreauth} → ${r.requiresPreauth}`);
    if (p.deductible !== r.deductible) changes.push(`deductible ${p.deductible ?? "—"} → ${r.deductible ?? "—"}`);
    for (const tier of r.tiers) {
      const pt = p.tiers.find((x) => x.networkTierId === tier.networkTierId);
      if (!pt) {
        changes.push(`tier ${tier.tierCode} added`);
        continue;
      }
      if (pt.copayFixed !== tier.copayFixed)
        changes.push(`${tier.tierCode} co-pay ${pt.copayFixed ?? "—"} → ${tier.copayFixed ?? "—"}`);
      if (pt.coinsurancePercent !== tier.coinsurancePercent)
        changes.push(`${tier.tierCode} co-insurance ${pt.coinsurancePercent ?? "—"} → ${tier.coinsurancePercent ?? "—"}`);
      if (pt.isCovered !== tier.isCovered) changes.push(`${tier.tierCode} covered ${pt.isCovered} → ${tier.isCovered}`);
    }
    if (changes.length > 0) lines.push({ category: k, kind: "changed", detail: changes.join("; ") });
  }
  for (const [k, p] of prev) {
    lines.push({ category: k, kind: "removed", detail: p.isCovered ? "was covered" : "was not covered" });
  }
  return lines;
}

function VersionDiff({ current, previous }: { current: PlanVersionView; previous: PlanVersionView | null }) {
  const t = useLoc();
  const lines = useMemo(() => (previous ? diffRules(current.rules, previous.rules) : []), [current, previous]);
  const label = { added: S.added, removed: S.removed, changed: S.changed } as const;

  return (
    <section data-testid="version-diff">
      <h4>{t(S.diff)}</h4>
      {!previous && <InlineAlert tone="info">{t(S.noDiff)}</InlineAlert>}
      {previous && lines.length === 0 && <InlineAlert tone="info">{t(S.unchanged)}</InlineAlert>}
      <ul className="pol-diff">
        {lines.map((l) => (
          <li key={`${l.category}-${l.kind}-${l.detail}`}>
            <StatusChip kind={l.kind === "removed" ? "bad" : l.kind === "added" ? "ok" : "info"} label={t(label[l.kind])} />
            <strong>{l.category}</strong> — {l.detail}
          </li>
        ))}
      </ul>
    </section>
  );
}
