import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  Card,
  DataTable,
  InlineAlert,
  InputField,
  SearchField,
  StatusChip,
  Tabs,
  TextareaField,
  useTheme,
} from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  CategoryCoverageDetail,
  MemberCoverageDetail,
  MemberGroupView,
  MemberQueryRow,
  MemberUtilizationView,
  PlanChangeView,
  PlanChangePreviewView,
  PolicyApi,
  PolicyPlanView,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, useLoc } from "./_shared";
import { ChangeTimeline, DocumentsPanel, LimitMeters, NotesPanel, useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";

/**
 * Phase 19.6 — member query and the member detail (design 38 §4.2 / §4.5 / §6).
 *
 * The detail is where every other module's output meets one person: the plan they are on and the VERSION of
 * it, the coverage that plan generated, the per-tier cost share they will actually be charged, what they have
 * consumed, the documents on file, the notes, and the change timeline. It is deliberately one screen — an
 * officer answering "why was this claim rejected" should not have to reconcile four.
 *
 * Every membership change is a dialog with an explicit effective date, because "when" is the field that
 * decides whether a member was covered on the day they were treated, and a change that silently means "today"
 * is how retroactive cover gets granted by accident.
 */

const S = {
  title: { en: "Members", ar: "الأعضاء" },
  search: { en: "Name, member number or identifier", ar: "الاسم أو رقم العضوية أو المعرّف" },
  find: { en: "Search", ar: "بحث" },
  memberNo: { en: "Member no.", ar: "رقم العضوية" },
  name: { en: "Name", ar: "الاسم" },
  plan: { en: "Plan", ar: "الخطة" },
  relationship: { en: "Relationship", ar: "صلة القرابة" },
  status: { en: "Status", ar: "الحالة" },
  from: { en: "From", ar: "من" },
  waiting: { en: "Waiting period", ar: "فترة الانتظار" },
  used: { en: "% used", ar: "٪ مستخدم" },
  noMembers: { en: "No members match this search.", ar: "لا يوجد أعضاء مطابقون." },
  select: { en: "Select a member to open their record.", ar: "اختر عضوًا لفتح سجله." },
  truncated: {
    en: "More people matched than could be resolved — this page is a subset, narrow the search.",
    ar: "تطابق عدد أكبر مما يمكن حصره — هذه الصفحة جزء من النتائج، ضيّق البحث.",
  },
  payerScoped: {
    en: "Narrowed to the payers you are assigned to — this is not the whole book.",
    ar: "تم التضييق على الجهات الممولة المسندة إليك — هذه ليست كل السجلات.",
  },
  tabCoverage: { en: "Coverage", ar: "التغطية" },
  tabUtilization: { en: "Utilization", ar: "الاستخدام" },
  tabNotes: { en: "Notes", ar: "الملاحظات" },
  tabDocuments: { en: "Documents", ar: "المستندات" },
  tabTimeline: { en: "Timeline", ar: "السجل" },
  planVersion: { en: "Plan version", ar: "إصدار الخطة" },
  enrolledUnder: { en: "Enrolled under version", ar: "مسجّل تحت الإصدار" },
  versionDrift: {
    en: "This member's entitlements were generated under an older version of the plan. That is legitimate — it is why two members of the same plan can have different ceilings.",
    ar: "أُنشئت استحقاقات هذا العضو تحت إصدار أقدم من الخطة. هذا سليم — ولهذا قد يختلف سقف عضوين في الخطة نفسها.",
  },
  category: { en: "Category", ar: "الفئة" },
  limit: { en: "Limit", ar: "الحد" },
  consumed: { en: "Consumed", ar: "المستهلك" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  resets: { en: "Resets", ar: "إعادة التعيين" },
  costShare: { en: "Cost share by network tier", ar: "مشاركة التكلفة حسب شريحة الشبكة" },
  tier: { en: "Tier", ar: "الشريحة" },
  copay: { en: "Co-pay", ar: "المشاركة الثابتة" },
  coinsurance: { en: "Co-insurance", ar: "نسبة المشاركة" },
  preauth: { en: "Pre-auth", ar: "موافقة مسبقة" },
  covered: { en: "Covered", ar: "مغطّى" },
  notCovered: { en: "Not covered", ar: "غير مغطّى" },
  limitDiffers: {
    en: "The ceiling on this member's coverage differs from what the plan in force would grant today.",
    ar: "يختلف سقف تغطية هذا العضو عمّا تمنحه الخطة السارية اليوم.",
  },
  exclusions: { en: "Exclusions", ar: "الاستثناءات" },
  terminate: { en: "Terminate", ar: "إنهاء" },
  openProfile: { en: "Open full profile", ar: "فتح الملف الكامل" },
  reinstate: { en: "Reinstate", ar: "إعادة تفعيل" },
  changeGroup: { en: "Change group", ar: "تغيير المجموعة" },
  changePlan: { en: "Change plan", ar: "تغيير الخطة" },
  effectiveDate: { en: "Effective date", ar: "تاريخ السريان" },
  reason: { en: "Reason", ar: "السبب" },
  reasonRequired: { en: "A reason is required.", ar: "السبب مطلوب." },
  reasonOptional: { en: "Reason (optional)", ar: "السبب (اختياري)" },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  group: { en: "Group", ar: "المجموعة" },
  none: { en: "No group", ar: "بدون مجموعة" },
  targetPlan: { en: "Move to plan", ar: "الانتقال إلى خطة" },
  carryPreview: { en: "What carries forward", ar: "ما ينتقل مع العضو" },
  carryHint: {
    en: "Consumption carries with the member. A member who has used 300 of 1,000 moving to a 500 plan has 200 left, not 500.",
    ar: "ينتقل الاستهلاك مع العضو. من استهلك ٣٠٠ من ١٠٠٠ وانتقل إلى خطة ٥٠٠ يتبقى له ٢٠٠ لا ٥٠٠.",
  },
  // The two readings of ADR-0020. Which one is in force is a SERVER setting, so the hint is chosen from the
  // preview's answer rather than hard-coded here — a screen that always claims consumption carries would be
  // stating the wrong rule outright on any deployment configured the other way.
  resetHint: {
    en: "Each plan carries its own ceiling here: the member starts the new plan at zero consumed.",
    ar: "لكل خطة سقفها الخاص هنا: يبدأ العضو الخطة الجديدة باستهلاك صفر.",
  },
  selectPlanFirst: { en: "Choose a plan to see what would change.", ar: "اختر خطة لعرض ما سيتغيّر." },
  currentLimit: { en: "Limit now", ar: "الحد الحالي" },
  newLimit: { en: "Limit after", ar: "الحد بعد التغيير" },
  notHeldToday: { en: "New benefit", ar: "منفعة جديدة" },
  dropped: { en: "Benefits that would be withdrawn", ar: "منافع ستُسحب" },
  droppedHint: {
    en: "The new plan does not cover these at all. The member holds them today.",
    ar: "الخطة الجديدة لا تغطي هذه إطلاقًا. العضو يملكها اليوم.",
  },
  previewing: { en: "Calculating…", ar: "جارٍ الحساب…" },
  carryResult: { en: "Applied — new balances", ar: "تم التطبيق — الأرصدة الجديدة" },
  exhausted: { en: "Exhausted", ar: "مستنفد" },
  backdated: {
    en: "This date is in the past. A back-dated membership change needs supervisory rights and will be refused without them.",
    ar: "هذا التاريخ في الماضي. يتطلب التغيير بأثر رجعي صلاحية إشرافية وسيُرفض بدونها.",
  },
  done: { en: "Change applied.", ar: "تم تطبيق التغيير." },
  utilizationCaption: { en: "Consumption against limit, by benefit category", ar: "الاستهلاك مقابل الحد، حسب فئة المنفعة" },
  reconcileBad: {
    en: "The accumulator and the reported total disagree. Treat these figures as provisional and raise it.",
    ar: "لا يتطابق المُراكِم مع الإجمالي المُبلَّغ. اعتبر هذه الأرقام مبدئية وأبلغ عنها.",
  },
  unavailable: { en: "Some figures could not be composed:", ar: "تعذّر تجميع بعض الأرقام:" },
  encounters: { en: "Encounters", ar: "الزيارات" },
  authorizations: { en: "Authorizations", ar: "التفويضات" },
  terminationReason: { en: "Termination reason", ar: "سبب الإنهاء" },
} satisfies Record<string, Localized>;

function statusKind(status: string): "ok" | "warn" | "bad" | "neu" | "info" {
  switch (status) {
    case "Active": return "ok";
    case "Suspended": return "warn";
    case "Terminated": case "Cancelled": return "bad";
    case "Pending": return "info";
    default: return "neu";
  }
}

// ── Member query ────────────────────────────────────────────────────────────────────────────────────────

export function MemberSearch({ api = createHttpPolicyApi() }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const [query, setQuery] = useState("");
  const [page, setPage] = useState<{
    items: MemberQueryRow[];
    payerScopeApplied: boolean;
    identityMatchTruncated: boolean;
    totalCount: number;
  } | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [selected, setSelected] = useState<MemberQueryRow | null>(null);

  const run = useCallback(
    async (name?: string) => {
      setError(null);
      try {
        setPage(await api.memberQuery({ name, pageSize: 50 }));
      } catch (e) {
        setError(writeErrorMessage(e).message);
      }
    },
    [api],
  );

  useEffect(() => {
    void run();
  }, [run]);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <Card>
        <div className="pol-searchbar">
          <SearchField
            aria-label={t(S.search)}
            placeholder={t(S.search)}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void run(query.trim() || undefined);
            }}
          />
          <Button variant="secondary" onClick={() => run(query.trim() || undefined)}>
            {t(S.find)}
          </Button>
        </div>
      </Card>

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {page?.payerScopeApplied && <InlineAlert tone="info">{t(S.payerScoped)}</InlineAlert>}
      {/* A truncated identity match makes the page a SUBSET. Saying so is the difference between a search
          and a wrong answer. */}
      {page?.identityMatchTruncated && <InlineAlert tone="warn">{t(S.truncated)}</InlineAlert>}

      <Card>
        <DataTable
          caption={t(S.title)}
          rows={page?.items ?? []}
          rowKey={(r) => r.enrollmentId}
          interactive
          selectedKey={selected?.enrollmentId ?? null}
          onSelect={(r) => setSelected(r)}
          loading={page === null && !error}
          emptyLabel={t(S.noMembers)}
          columns={[
            { key: "memberNo", header: t(S.memberNo), cell: (r) => r.memberNo },
            {
              key: "name",
              header: t(S.name),
              // A blank name is legible; a wrong one is not. patient-service could not be asked → null.
              cell: (r) => [r.givenName, r.familyName].filter(Boolean).join(" ") || "—",
            },
            { key: "plan", header: t(S.plan), cell: (r) => r.planLabel ?? "—" },
            { key: "relationship", header: t(S.relationship), cell: (r) => r.relationship },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={statusKind(r.status)} label={r.status} /> },
            { key: "from", header: t(S.from), cell: (r) => fmt.date(r.effectiveFrom) },
            {
              key: "waiting",
              header: t(S.waiting),
              cell: (r) => <StatusChip kind={r.waitingPeriodState === "Serving" ? "warn" : "neu"} label={r.waitingPeriodState} />,
            },
            {
              key: "used",
              header: t(S.used),
              cell: (r) => (r.percentUsed != null ? `${Math.round(r.percentUsed)}%` : "—"),
            },
          ]}
        />
      </Card>

      {!selected && <InlineAlert tone="info">{t(S.select)}</InlineAlert>}
      {selected && <MemberDetail api={api} row={selected} onChanged={() => run(query.trim() || undefined)} />}
    </div>
  );
}

// ── Member detail ───────────────────────────────────────────────────────────────────────────────────────

type Dialog = "terminate" | "reinstate" | "changeGroup" | "changePlan" | null;

export function MemberDetail({
  api,
  row,
  onChanged,
}: {
  api: PolicyApi;
  row: MemberQueryRow;
  onChanged: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const { lang } = useTheme();
  const [tab, setTab] = useState("coverage");
  const [dialog, setDialog] = useState<Dialog>(null);
  const [announce, setAnnounce] = useState("");
  const [coverage, setCoverage] = useState<MemberCoverageDetail | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  const loadCoverage = useCallback(async () => {
    try {
      setCoverage(await api.coverageDetails(row.enrollmentId));
    } catch (e) {
      setError(writeErrorMessage(e).message);
    }
  }, [api, row.enrollmentId]);

  useEffect(() => {
    setCoverage(null);
    void loadCoverage();
  }, [loadCoverage]);

  return (
    <div className="pol-detail" data-testid="member-detail">
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>

      <Card>
        <div className="pol-identity">
          <h2>
            {[row.givenName, row.familyName].filter(Boolean).join(" ") || row.memberNo}
          </h2>
          <StatusChip kind={statusKind(row.status)} label={row.status} />
          <dl>
            <div>
              <dt>{t(S.memberNo)}</dt>
              <dd>{row.memberNo}</dd>
            </div>
            <div>
              <dt>{t(S.plan)}</dt>
              <dd>
                {row.planLabel ?? "—"}
                {coverage?.planVersionNo != null && ` · ${t(S.planVersion)} ${coverage.planVersionNo}`}
              </dd>
            </div>
            <div>
              <dt>{t(S.relationship)}</dt>
              <dd>{row.relationship}</dd>
            </div>
            <div>
              <dt>{t(S.from)}</dt>
              <dd>
                {fmt.date(row.effectiveFrom)} → {row.effectiveTo ? fmt.date(row.effectiveTo) : "—"}
              </dd>
            </div>
            {/* Projected only for case-handling roles; a null here means the caller is not entitled to it,
                not that the membership ended for no reason. */}
            {row.terminationReason && (
              <div>
                <dt>{t(S.terminationReason)}</dt>
                <dd>{row.terminationReason}</dd>
              </div>
            )}
          </dl>
        </div>

        <div className="pol-actions">
          {/* Phase 20 — search result into the unified profile. One route for every role: the SERVER decides
              what comes back, so a beneficiary-management officer and a clinician follow the same link and
              receive different records. */}
          <a className="profile-action-link" href={`/patients/${encodeURIComponent(row.beneficiaryId)}`}>
            {t(S.openProfile)}
          </a>
          <Button variant="secondary" onClick={() => setDialog("terminate")}>{t(S.terminate)}</Button>
          <Button variant="secondary" onClick={() => setDialog("reinstate")}>{t(S.reinstate)}</Button>
          <Button variant="secondary" onClick={() => setDialog("changeGroup")}>{t(S.changeGroup)}</Button>
          <Button variant="secondary" onClick={() => setDialog("changePlan")}>{t(S.changePlan)}</Button>
        </div>
      </Card>

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {dialog && (
        <MembershipDialog
          api={api}
          kind={dialog}
          row={row}
          onClose={() => setDialog(null)}
          onDone={async (msg) => {
            setAnnounce(msg);
            setDialog(null);
            await loadCoverage();
            onChanged();
          }}
        />
      )}

      <Tabs
        aria-label={t(S.title)}
        value={tab}
        onValueChange={setTab}
        items={[
          { value: "coverage", label: t(S.tabCoverage), content: <CoverageTab coverage={coverage} /> },
          { value: "utilization", label: t(S.tabUtilization), content: tab === "utilization" ? <MemberUtilizationTab api={api} beneficiaryId={row.beneficiaryId} /> : null },
          { value: "notes", label: t(S.tabNotes), content: tab === "notes" ? <NotesPanel api={api} scope="enrollments" scopeRef={row.enrollmentId} /> : null },
          { value: "documents", label: t(S.tabDocuments), content: tab === "documents" ? <DocumentsPanel api={api} scope="enrollments" scopeRef={row.enrollmentId} /> : null },
          { value: "timeline", label: t(S.tabTimeline), content: tab === "timeline" ? <ChangeTimeline api={api} scope="enrollments" scopeRef={row.enrollmentId} lang={lang} /> : null },
        ]}
      />
    </div>
  );
}

// ── Coverage + cost-share grid ──────────────────────────────────────────────────────────────────────────

function CoverageTab({ coverage }: { coverage: MemberCoverageDetail | null }) {
  const t = useLoc();
  const fmt = useFormat();
  const [open, setOpen] = useState<string | null>(null);
  if (!coverage) return null;

  return (
    <Card data-testid="coverage-tab">
      {coverage.planVersionChangedSinceEnrolment && (
        <InlineAlert tone="info" data-testid="version-drift">
          {t(S.versionDrift)} ({t(S.enrolledUnder)} {coverage.enrolledUnderPlanVersionId?.slice(0, 8)})
        </InlineAlert>
      )}
      <table className="pol-grid">
        <caption className="sr-only">{t(S.tabCoverage)}</caption>
        <thead>
          <tr>
            <th scope="col">{t(S.category)}</th>
            <th scope="col">{t(S.covered)}</th>
            <th scope="col">{t(S.limit)}</th>
            <th scope="col">{t(S.consumed)}</th>
            <th scope="col">{t(S.remaining)}</th>
            <th scope="col">{t(S.resets)}</th>
            <th scope="col">{t(S.costShare)}</th>
          </tr>
        </thead>
        <tbody>
          {coverage.categories.map((c) => [
            <tr key={c.benefitCategoryCode}>
              <th scope="row">
                {c.benefitCategoryCode}
                {c.limitDiffersFromPlan && <StatusChip kind="info" label="≠" />}
              </th>
              <td>
                <StatusChip kind={c.isCovered ? "ok" : "neu"} label={c.isCovered ? t(S.covered) : t(S.notCovered)} />
              </td>
              <td>{c.limit != null ? fmt.money(c.limit) : "∞"}</td>
              <td>{fmt.money(c.consumed)}</td>
              <td>{c.remaining != null ? fmt.money(c.remaining) : "∞"}</td>
              <td>{c.resetsOn ? fmt.date(c.resetsOn) : c.resetPeriod}</td>
              <td>
                <Button
                  variant="ghost"
                  aria-expanded={open === c.benefitCategoryCode}
                  onClick={() => setOpen(open === c.benefitCategoryCode ? null : c.benefitCategoryCode)}
                >
                  {t(S.costShare)}
                </Button>
              </td>
            </tr>,
            open === c.benefitCategoryCode ? (
              <tr key={`${c.benefitCategoryCode}-cs`} className="pol-grid-sub">
                <td colSpan={7}>
                  <CostShareGrid category={c} />
                </td>
              </tr>
            ) : null,
          ])}
        </tbody>
      </table>
    </Card>
  );
}

function CostShareGrid({ category }: { category: CategoryCoverageDetail }) {
  const t = useLoc();
  const fmt = useFormat();
  return (
    <div>
      {category.limitDiffersFromPlan && <InlineAlert tone="info">{t(S.limitDiffers)}</InlineAlert>}
      <table className="pol-costshare">
        <caption>
          {t(S.costShare)} — {category.benefitCategoryCode}
        </caption>
        <thead>
          <tr>
            <th scope="col">{t(S.tier)}</th>
            <th scope="col">{t(S.covered)}</th>
            <th scope="col">{t(S.copay)}</th>
            <th scope="col">{t(S.coinsurance)}</th>
            <th scope="col">{t(S.preauth)}</th>
            <th scope="col">{t(S.limit)}</th>
          </tr>
        </thead>
        <tbody>
          {category.costShareByTier.map((x) => (
            <tr key={x.networkTierId}>
              <th scope="row">{x.tierCode}</th>
              <td>
                <StatusChip kind={x.isCovered ? "ok" : "neu"} label={x.isCovered ? t(S.covered) : t(S.notCovered)} />
              </td>
              <td>{x.copayFixed != null ? fmt.money(x.copayFixed) : x.copayPercent != null ? `${x.copayPercent}%` : "—"}</td>
              <td>{x.coinsurancePercent != null ? `${x.coinsurancePercent}%` : "—"}</td>
              <td>
                <StatusChip kind={x.requiresPreauth ? "warn" : "neu"} label={x.requiresPreauth ? t(S.preauth) : "—"} />
              </td>
              <td>{x.limitAtTier != null ? fmt.money(x.limitAtTier) : "∞"}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {category.exclusions.length > 0 && (
        <p>
          <strong>{t(S.exclusions)}:</strong> {category.exclusions.join(", ")}
        </p>
      )}
    </div>
  );
}

// ── Member utilization ──────────────────────────────────────────────────────────────────────────────────

function MemberUtilizationTab({ api, beneficiaryId }: { api: PolicyApi; beneficiaryId: string }) {
  const t = useLoc();
  const fmt = useFormat();
  const [view, setView] = useState<MemberUtilizationView | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    api
      .memberUtilization(beneficiaryId)
      .then((v) => live && setView(v))
      .catch((e) => live && setError(writeErrorMessage(e).message));
    return () => { live = false; };
  }, [api, beneficiaryId]);

  const meters = useMemo(
    () =>
      (view?.categories ?? []).map((c) => ({
        label: c.benefitCategory,
        consumed: c.consumed,
        limit: c.unlimited ? null : c.limit,
        valueText: fmt.money(c.consumed),
        limitText: c.unlimited ? "∞" : fmt.money(c.limit),
      })),
    [view, fmt],
  );

  return (
    <Card data-testid="member-utilization">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {view && (
        <>
          {!view.reconciliation.reconciled && <InlineAlert tone="bad">{t(S.reconcileBad)}</InlineAlert>}
          <LimitMeters caption={t(S.utilizationCaption)} rows={meters} />
          <dl className="pol-kpis">
            <div>
              <dt>{t(S.encounters)}</dt>
              {/* null means "could not ask", never "zero" — an em dash says so. */}
              <dd>{fmt.number(view.external.encounters ?? undefined)}</dd>
            </div>
            <div>
              <dt>{t(S.authorizations)}</dt>
              <dd>{fmt.number(view.external.authorizationsRaised ?? undefined)}</dd>
            </div>
          </dl>
          {view.external.unavailable.length > 0 && (
            <InlineAlert tone="warn">
              {t(S.unavailable)} {view.external.unavailable.join(", ")}
            </InlineAlert>
          )}
        </>
      )}
    </Card>
  );
}

// ── Membership dialogs ──────────────────────────────────────────────────────────────────────────────────

const todayIso = () => new Date().toISOString().slice(0, 10);

function MembershipDialog({
  api,
  kind,
  row,
  onClose,
  onDone,
}: {
  api: PolicyApi;
  kind: Exclude<Dialog, null>;
  row: MemberQueryRow;
  onClose: () => void;
  onDone: (announcement: string) => Promise<void>;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [effectiveDate, setEffectiveDate] = useState(todayIso());
  const [reason, setReason] = useState("");
  const [groupId, setGroupId] = useState("");
  const [policyPlanId, setPolicyPlanId] = useState("");
  const [groups, setGroups] = useState<MemberGroupView[]>([]);
  const [plans, setPlans] = useState<PolicyPlanView[]>([]);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [applied, setApplied] = useState<PlanChangeView | null>(null);
  const [preview, setPreview] = useState<PlanChangePreviewView | null>(null);
  const [previewError, setPreviewError] = useState<Localized | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [key, rotateKey] = useIdempotencyKey();

  const reasonMandatory = kind === "terminate" || kind === "changePlan";
  const backdated = effectiveDate < todayIso();

  useEffect(() => {
    if (kind === "changeGroup") api.policyGroups(row.policyId).then(setGroups).catch(() => setGroups([]));
    if (kind === "changePlan") api.policyPlans(row.policyId).then(setPlans).catch(() => setPlans([]));
  }, [api, kind, row.policyId]);

  // The dry run. Re-asked whenever the target plan or the effective date changes, because both are inputs to
  // the answer — a plan not yet in force on the chosen date fails HERE, in a preview, rather than after the
  // officer has written a justification for a change that was never going to be accepted.
  useEffect(() => {
    if (kind !== "changePlan" || !policyPlanId) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    let live = true;
    setPreviewing(true);
    setPreviewError(null);
    api
      .previewPlanChange(row.enrollmentId, policyPlanId, effectiveDate)
      .then((p) => {
        if (!live) return;
        setPreview(p);
      })
      .catch((e: unknown) => {
        if (!live) return;
        // A failed preview clears the previous one. Leaving the last successful answer on screen beside a newly
        // chosen plan is how somebody confirms a change against arithmetic for a different plan.
        setPreview(null);
        setPreviewError(writeErrorMessage(e).message);
      })
      .finally(() => {
        if (live) setPreviewing(false);
      });
    return () => {
      live = false;
    };
  }, [api, kind, policyPlanId, effectiveDate, row.enrollmentId]);

  const title = { terminate: S.terminate, reinstate: S.reinstate, changeGroup: S.changeGroup, changePlan: S.changePlan }[kind];

  async function submit() {
    if (reasonMandatory && !reason.trim()) {
      setError(S.reasonRequired);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      switch (kind) {
        case "terminate":
          await api.terminate(row.enrollmentId, effectiveDate, reason.trim(), key);
          break;
        case "reinstate":
          await api.reinstate(row.enrollmentId, effectiveDate, reason.trim() || null, key);
          break;
        case "changeGroup":
          await api.changeGroup(row.enrollmentId, groupId || null, effectiveDate, reason.trim() || null, key);
          break;
        case "changePlan": {
          const result = await api.changePlan(row.enrollmentId, policyPlanId, effectiveDate, reason.trim(), key);
          // The authoritative arithmetic comes back from the server. Shown before the dialog closes, because
          // "what is this member's ceiling now" is the question the change was made to answer.
          setApplied(result);
          rotateKey();
          setBusy(false);
          return;
        }
      }
      rotateKey();
      await onDone(t(S.done));
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="pol-dialog" role="dialog" aria-modal="true" aria-label={t(title)} data-testid={`dialog-${kind}`}>
      <h3>{t(title)}</h3>

      <InputField
        type="date"
        label={t(S.effectiveDate)}
        value={effectiveDate}
        onChange={(e) => setEffectiveDate(e.target.value)}
      />
      {/* Stated up front rather than discovered as a 403. The server decides; this only removes the surprise. */}
      {backdated && <InlineAlert tone="warn">{t(S.backdated)}</InlineAlert>}

      {kind === "changeGroup" && (
        <>
          <label htmlFor="dlg-group">{t(S.group)}</label>
          <select id="dlg-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
            <option value="">{t(S.none)}</option>
            {groups.map((g) => (
              <option key={g.groupId} value={g.groupId}>
                {g.groupCode} — {g.nameEn}
              </option>
            ))}
          </select>
        </>
      )}

      {kind === "changePlan" && (
        <>
          <label htmlFor="dlg-plan">{t(S.targetPlan)}</label>
          <select id="dlg-plan" value={policyPlanId} onChange={(e) => setPolicyPlanId(e.target.value)}>
            <option value="">—</option>
            {plans.map((p) => (
              <option key={p.policyPlanId} value={p.policyPlanId}>
                {p.planLabel}
              </option>
            ))}
          </select>

          {/* The carry-forward preview — the server's own dry run, not an estimate assembled here. Same
              resolution and same arithmetic as the change itself, so what this shows is what will happen. */}
          {!policyPlanId && <p className="pol-muted">{t(S.selectPlanFirst)}</p>}
          {previewing && <p className="pol-muted" aria-live="polite">{t(S.previewing)}</p>}
          {previewError && <InlineAlert tone="bad" data-testid="preview-error">{t(previewError)}</InlineAlert>}

          {preview && (
            <div data-testid="carry-preview">
              <h4>{t(S.carryPreview)}</h4>
              <InlineAlert tone="info">
                {t(preview.consumptionPolicy === "CarryForward" ? S.carryHint : S.resetHint)}
              </InlineAlert>
              <table className="pol-costshare">
                <caption className="sr-only">{t(S.carryPreview)}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t(S.category)}</th>
                    <th scope="col">{t(S.currentLimit)}</th>
                    <th scope="col">{t(S.consumed)}</th>
                    <th scope="col">{t(S.newLimit)}</th>
                    <th scope="col">{t(S.remaining)}</th>
                  </tr>
                </thead>
                <tbody>
                  {preview.rows.map((r) => (
                    <tr key={r.benefitCategoryId}>
                      <th scope="row">{r.benefitCategoryCode ?? r.benefitCategoryId.slice(0, 8)}</th>
                      {/* "Not covered today" and "unbounded today" are different facts and a dash cannot say
                          both — `held` is what separates them. */}
                      <td>{!r.held ? t(S.notHeldToday) : r.currentLimitValue != null ? fmt.money(r.currentLimitValue) : "∞"}</td>
                      <td>{fmt.money(r.consumedValue)}</td>
                      <td>{r.newLimitValue != null ? fmt.money(r.newLimitValue) : "∞"}</td>
                      <td>
                        {r.remaining != null ? fmt.money(r.remaining) : "∞"}
                        {r.exhausted && <StatusChip kind="bad" label={t(S.exhausted)} />}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>

              {/* The half no client-side estimate could have recovered: a benefit the new plan does not cover
                  produces no row in the outcome at all, so without this it simply disappears. */}
              {preview.droppedCategories.length > 0 && (
                <div data-testid="carry-dropped">
                  <h4>{t(S.dropped)}</h4>
                  <InlineAlert tone="warn">{t(S.droppedHint)}</InlineAlert>
                  <table className="pol-costshare">
                    <caption className="sr-only">{t(S.dropped)}</caption>
                    <thead>
                      <tr>
                        <th scope="col">{t(S.category)}</th>
                        <th scope="col">{t(S.currentLimit)}</th>
                        <th scope="col">{t(S.consumed)}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {preview.droppedCategories.map((d) => (
                        <tr key={d.benefitCategoryId}>
                          <th scope="row">{d.benefitCategoryCode ?? d.benefitCategoryId.slice(0, 8)}</th>
                          <td>{d.currentLimitValue != null ? fmt.money(d.currentLimitValue) : "∞"}</td>
                          <td>{fmt.money(d.consumedValue)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </>
      )}

      <TextareaField
        label={reasonMandatory ? t(S.reason) : t(S.reasonOptional)}
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        rows={2}
        required={reasonMandatory}
      />

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {applied && (
        <div data-testid="carry-result" aria-live="polite">
          <h4>{t(S.carryResult)}</h4>
          <table className="pol-costshare">
            <caption className="sr-only">{t(S.carryResult)}</caption>
            <thead>
              <tr>
                <th scope="col">{t(S.category)}</th>
                <th scope="col">{t(S.limit)}</th>
                <th scope="col">{t(S.consumed)}</th>
                <th scope="col">{t(S.remaining)}</th>
              </tr>
            </thead>
            <tbody>
              {applied.carriedLimits.map((c) => (
                <tr key={c.benefitCategoryId}>
                  <th scope="row">{c.benefitCategoryCode ?? c.benefitCategoryId.slice(0, 8)}</th>
                  <td>{c.limitValue != null ? fmt.money(c.limitValue) : "∞"}</td>
                  <td>{fmt.money(c.consumedValue)}</td>
                  <td>
                    {c.remaining != null ? fmt.money(c.remaining) : "∞"}
                    {c.exhausted && <StatusChip kind="bad" label={t(S.exhausted)} />}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="pol-dialog-actions">
        {!applied && (
          // A plan change cannot be confirmed until the dry run has answered. Not defensiveness about the
          // network: the preview runs the same resolution the change does, so a preview that failed is a change
          // that would have failed — and the point of the dialog is that nobody moves a member's entitlement
          // without having been shown what it does to them.
          <Button variant="primary" onClick={submit} disabled={busy || (kind === "changePlan" && !preview)}>
            {t(S.confirm)}
          </Button>
        )}
        <Button variant="ghost" onClick={() => (applied ? void onDone(t(S.done)) : onClose())}>
          {applied ? t(S.confirm) : t(S.cancel)}
        </Button>
      </div>
    </Card>
  );
}
