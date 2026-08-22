import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, Card, CheckboxField, ComboboxField, DataTable, DataTableView, Icon, InlineAlert, InputField, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type { NetworkTierView, PolicyApi, TierAssignmentView, TierProviderOption, TierResolutionView } from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerTiers } from "../authz/permissions";
import { PageHeader, fillLocalized, useLoc, readErrorMessage } from "./_shared";
import { ConfirmAction } from "./ConfirmAction";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { useEnumLabel } from "../i18n/enumLabels";

/**
 * Phase 19.6 (19.1b) — network tier administration.
 *
 * TWO ROLES, ONE SCREEN, DIFFERENT AUTHORITY. The Network Team decides WHICH tier a provider sits in; policy
 * administration decides what a member pays AT a tier and must be able to see the tiers it is pricing
 * against. So policy admins reach this screen read-only, and the write affordances are simply absent rather
 * than present-and-refused (`mayAdministerTiers` mirrors provider-service's `NetworkTierGate`).
 *
 * The RESOLVE-AT-DATE tool is not a convenience. Tier assignment is effective-dated and most-specific-wins,
 * which means "which tier applies" is a question with a different answer on different days, and an
 * adjudication nobody can reproduce is one nobody can defend. The tool answers it for a date, and shows the
 * BASIS — "assigned to the out-of-network tier" and "out-of-network because nothing was assigned" price the
 * same and need very different follow-up.
 */

const S = {
  title: { en: "Network Tiers", ar: "شرائح الشبكة" },
  readOnly: {
    en: "Read-only. The Network Team owns the tier structure; policy administration prices benefits at a tier.",
    ar: "للقراءة فقط. تملك الشبكة هيكل الشرائح؛ وتتولى إدارة الوثائق تسعير المنافع عند الشريحة.",
  },
  code: { en: "Code", ar: "الرمز" },
  name: { en: "Name", ar: "الاسم" },
  rank: { en: "Rank", ar: "الترتيب" },
  oon: { en: "Out of network", ar: "خارج الشبكة" },
  status: { en: "Status", ar: "الحالة" },
  noTiers: { en: "No network tiers defined.", ar: "لا توجد شرائح شبكة." },
  newTier: { en: "New tier", ar: "شريحة جديدة" },
  create: { en: "Create tier", ar: "إنشاء شريحة" },
  nameEn: { en: "Name (English)", ar: "الاسم (إنجليزي)" },
  nameAr: { en: "Name (Arabic)", ar: "الاسم (عربي)" },
  codeHint: {
    en: "The code and the out-of-network flag can never be changed: adjudicated claims and priced benefit rules already refer to them. Retire the tier and create the right one.",
    ar: "لا يمكن تغيير الرمز ولا علامة خارج الشبكة: المطالبات المُقيَّمة وقواعد المنافع المسعّرة تشير إليهما. أوقف الشريحة وأنشئ الصحيحة.",
  },
  assignments: { en: "Assignments", ar: "الإسنادات" },
  scope: { en: "Scope", ar: "النطاق" },
  scopeRef: { en: "Reference", ar: "المرجع" },
  window: { en: "In force", ar: "سارٍ" },
  noAssignments: { en: "No assignments on this tier.", ar: "لا توجد إسنادات على هذه الشريحة." },
  revoke: { en: "Revoke", ar: "سحب" },
  search: { en: "Search", ar: "بحث" },
  assignmentSearchHint: { en: "Scope or reference", ar: "النطاق أو المرجع" },
  noMatches: { en: "No assignments match your search.", ar: "لا توجد إسنادات مطابقة لبحثك." },
  resolve: { en: "Resolve tier at a date", ar: "تحديد الشريحة في تاريخ" },
  providerId: { en: "Provider", ar: "مقدم الخدمة" },
  locationId: { en: "Location (optional)", ar: "الموقع (اختياري)" },
  serviceDate: { en: "Service date", ar: "تاريخ الخدمة" },
  run: { en: "Resolve", ar: "تحديد" },
  basis: { en: "Basis", ar: "الأساس" },
  resolvedTo: { en: "Resolves to", ar: "يُحدَّد إلى" },
  selectTier: { en: "Select a tier to see its assignments.", ar: "اختر شريحة لعرض إسناداتها." },
  created: { en: "Tier created.", ar: "تم إنشاء الشريحة." },
  revoked: { en: "Assignment revoked.", ar: "تم سحب الإسناد." },
  // ── Confirming a revoke ───────────────────────────────────────────────────────────────────────────────
  // This fired straight from the click, on a `ghost` button — the transparent variant every Cancel in the app
  // wears. Which tier a provider sits in decides the rate their claims are priced at, so the row it removes is
  // not a display preference.
  revokeTitle: { en: "Revoke this assignment?", ar: "سحب هذا الإسناد؟" },
  revokeBody: {
    en: "{0} will stop being assigned to this tier. Services on or after today price against whatever tier resolves instead.",
    ar: "سيتوقف إسناد {0} إلى هذه الشريحة. تُسعَّر الخدمات من اليوم فصاعدًا وفق الشريحة البديلة.",
  },
  revokeReversible: {
    en: "The assignment can be re-created, but claims already priced are not repriced.",
    ar: "يمكن إعادة إنشاء الإسناد، لكن المطالبات المُسعَّرة سابقًا لا يُعاد تسعيرها.",
  },

  // ---- 33.7 — creating the assignment the sentence above promised could be re-created ----
  assign: { en: "Assign to this tier", ar: "إسناد إلى هذه الشريحة" },
  assignHint: {
    en: "Which tier a provider sits in decides the rate their claims price at, from the date you set here. An assignment is effective-dated and the most specific one wins — a location assignment beats a provider-wide one for services delivered there.",
    ar: "تحدد شريحة مقدم الخدمة سعر تسعير مطالباته، اعتباراً من التاريخ الذي تحدده هنا. الإسناد مؤرَّخ السريان والأكثر تحديداً يسبق — فإسناد الموقع يسبق الإسناد على مستوى مقدم الخدمة للخدمات المقدَّمة فيه.",
  },
  provider: { en: "Provider", ar: "مقدم الخدمة" },
  pickProvider: { en: "Select a provider", ar: "اختر مقدم خدمة" },
  scopeLevel: { en: "Applies to", ar: "ينطبق على" },
  scopeProvider: { en: "The whole provider", ar: "مقدم الخدمة بالكامل" },
  scopeLocation: { en: "One location", ar: "موقع واحد" },
  scopeServiceLine: { en: "One contract service line", ar: "بند خدمة في العقد" },
  reference: { en: "Location or service-line id", ar: "معرّف الموقع أو بند الخدمة" },
  referenceHint: {
    en: "Copy it from the contract or the provider's locations. A provider-wide assignment needs no reference.",
    ar: "انسخه من العقد أو من مواقع مقدم الخدمة. الإسناد على مستوى مقدم الخدمة لا يحتاج مرجعاً.",
  },
  from: { en: "In force from", ar: "سارٍ من" },
  until: { en: "Until (optional, exclusive)", ar: "حتى (اختياري، غير شامل)" },
  assignNeedsProvider: { en: "Choose a provider.", ar: "اختر مقدم خدمة." },
  assignNeedsRef: {
    en: "A location or service-line assignment needs the id it applies to.",
    ar: "يحتاج إسناد الموقع أو بند الخدمة إلى المعرّف الذي ينطبق عليه.",
  },
  assigned: { en: "Assigned. Services from that date price at this tier.", ar: "تم الإسناد. تُسعَّر الخدمات من ذلك التاريخ عند هذه الشريحة." },
  retiredNoAssign: {
    en: "A retired tier takes no new assignments. Its existing ones stay readable, because claims priced against it must still render.",
    ar: "الشريحة المتوقفة لا تقبل إسنادات جديدة. وتبقى إسناداتها السابقة قابلة للقراءة، لأن المطالبات المسعّرة عندها يجب أن تظل ظاهرة.",
  },

  // ---- 33.7 — editing what a tier is CALLED, which was create-only ----
  edit: { en: "Rename this tier", ar: "إعادة تسمية الشريحة" },
  save: { en: "Save", ar: "حفظ" },
  saved: { en: "Tier updated.", ar: "تم تحديث الشريحة." },
  editHint: {
    en: "The name, rank and description can be corrected. The code and the out-of-network flag cannot — priced claims and benefit rules already refer to them.",
    ar: "يمكن تصحيح الاسم والترتيب والوصف. أما الرمز وعلامة خارج الشبكة فلا — إذ تشير إليهما المطالبات المسعّرة وقواعد المنافع.",
  },
  description: { en: "Description", ar: "الوصف" },

  // The actions column had NO header, which axe reports as `empty-table-header` and a screen-reader user
  // hears as a nameless column. The same latent issue two other tables carried before 32.6.
  actions: { en: "Actions", ar: "إجراءات" },
} satisfies Record<string, Localized>;

/**
 * The three levels a tier assignment can attach to, in the resolver's own vocabulary.
 *
 * The picker below is built from this rather than restating it: `NetworkAssignmentScope` is a server enum
 * and a list the client keeps in two places is one that eventually disagrees with itself.
 */
const ASSIGNMENT_SCOPES = [
  { value: "Provider", label: () => S.scopeProvider },
  { value: "Location", label: () => S.scopeLocation },
  { value: "ContractServiceLine", label: () => S.scopeServiceLine },
] as const;
type AssignmentScope = (typeof ASSIGNMENT_SCOPES)[number]["value"];

export function NetworkTiers({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const { session } = useAuth();
  const mayWrite = mayAdministerTiers(session?.issuerRoles);

  const [tiers, setTiers] = useState<NetworkTierView[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [assignments, setAssignments] = useState<TierAssignmentView[]>([]);
  /** The assignment awaiting confirmation — see `revokeTitle`. */
  const [revoking, setRevoking] = useState<TierAssignmentView | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  const [createKey, rotateCreateKey] = useIdempotencyKey();

  const [tierCode, setTierCode] = useState("");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [rank, setRank] = useState("1");
  const [isOon, setIsOon] = useState(false);

  const load = useCallback(async () => {
    try {
      setTiers(await api.networkTiers());
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!selected) return;
    let live = true;
    api
      .tierAssignments(selected)
      .then((a) => live && setAssignments(a))
      .catch((e) => live && setError(writeErrorMessage(e).message));
    return () => { live = false; };
  }, [api, selected]);

  /*
    Hoisted out of the JSX because `useTableQuery` sorts by `column.sortValue` and therefore needs the same
    array the table renders — one definition, not two that can drift apart.
  */
  const assignmentCols: Column<TierAssignmentView>[] = useMemo(() => [
            { key: "scope", header: t(S.scope), cell: (r) => r.scope, sortable: true, sortValue: (r) => r.scope },
            { key: "ref", header: t(S.scopeRef), cell: (r) => r.scopeRef.slice(0, 8) },
            {
              key: "window",
              header: t(S.window),
              cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}`,
            },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={enumLabel(r.status)} /> },
            {
              key: "act",
              header: t(S.actions),
              cell: (r) =>
                mayWrite && r.status === "Active" ? (
                  <Button
                    variant="danger"
                    size="sm"
                    onClick={() => setRevoking(r)}
                  >
                    {t(S.revoke)}
                  </Button>
                ) : null,
            },
  ], [t, fmt, mayWrite, enumLabel]);

  /** The row the assignment and rename panels act on — resolved once rather than looked up in three places. */
  const selectedTier = useMemo(
    () => (tiers ?? []).find((x) => x.networkTierId === selected) ?? null,
    [tiers, selected],
  );

  /** A tier accumulates assignments as the network grows; searched by the reference a contract cites. */
  const assignmentQuery = useTableQuery<TierAssignmentView>({
    rows: assignments,
    columns: assignmentCols,
    searchText: (r) => [r.scope, r.scopeRef, r.status].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.assignmentSearchHint),
    pageSize: 25,
    persistKey: "tier-assignments",
  });

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {!mayWrite && <InlineAlert tone="info" data-testid="tiers-read-only">{t(S.readOnly)}</InlineAlert>}
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <Card>
        <DataTable
          caption={t(S.title)}
          rows={tiers ?? []}
          rowKey={(r) => r.networkTierId}
          interactive
          selectedKey={selected}
          onSelect={(r) => setSelected(r.networkTierId)}
          loading={tiers === null && !error}
          emptyLabel={t(S.noTiers)}
          columns={[
            { key: "code", header: t(S.code), cell: (r) => r.tierCode, sortable: true, sortValue: (r) => r.tierCode },
            { key: "name", header: t(S.name), cell: (r) => r.nameEn, sortable: true, sortValue: (r) => r.nameEn },
            { key: "rank", header: t(S.rank), cell: (r) => r.rank, sortable: true, sortValue: (r) => r.rank },
            {
              key: "oon",
              header: t(S.oon),
              cell: (r) => <StatusChip kind={r.isOutOfNetwork ? "warn" : "ok"} label={r.isOutOfNetwork ? t(S.oon) : "—"} />, sortable: true, sortValue: (r) => Number(r.isOutOfNetwork) },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={enumLabel(r.status)} /> },
          ]}
        />
      </Card>

      {mayWrite && (
        <Card data-testid="tier-create">
          <h2 className="panel-h">{t(S.newTier)}</h2>
          <InlineAlert tone="info">{t(S.codeHint)}</InlineAlert>
          <InputField label={t(S.code)} value={tierCode} onChange={(e) => setTierCode(e.target.value)} />
          <InputField label={t(S.nameEn)} value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
          <InputField label={t(S.nameAr)} value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
          <InputField label={t(S.rank)} value={rank} inputMode="numeric" onChange={(e) => setRank(e.target.value)} />
          <CheckboxField
            label={t(S.oon)}
            checked={isOon}
            onChange={(e) => setIsOon(e.currentTarget.checked)}
          />
          <Button leadingIcon={<Icon name="plus" />}
            variant="primary"
            onClick={async () => {
              try {
                await api.createTier(
                  { tierCode, nameEn, nameAr, rank: Number(rank || "1"), description: null, isOutOfNetwork: isOon },
                  createKey,
                );
                rotateCreateKey();
                setTierCode(""); setNameEn(""); setNameAr("");
                setAnnounce(t(S.created));
                await load();
              } catch (e) {
                setError(writeErrorMessage(e).message);
              }
            }}
          >
            {t(S.create)}
          </Button>
        </Card>
      )}

      {!selected && <InlineAlert tone="info">{t(S.selectTier)}</InlineAlert>}

      {selected && (
        <Card>
          <h2 className="panel-h">{t(S.assignments)}</h2>
          <DataTableView
            query={assignmentQuery}
            rowKey={(r) => r.assignmentId}
            caption={t(S.assignments)}
            emptyLabel={t(S.noAssignments)}
            noMatchesLabel={t(S.noMatches)}
            columns={assignmentCols}
          />
        </Card>
      )}

      {/*
        33.7 — the assignment could be REVOKED and never created.

        The revoke dialog's own consequence line says "The assignment can be re-created, but claims already
        priced are not repriced", and there was nothing in the platform that could re-create one:
        `assignTier` was implemented in policyApi and called by nobody, so this screen removed rows from a
        table only some other, non-existent screen could ever fill. Both halves of the tier map — the tiers
        and who is in them — are administered here, or neither is.
      */}
      {mayWrite && selectedTier && (
        <AssignToTier
          api={api}
          tier={selectedTier}
          onAssigned={async () => {
            setAnnounce(t(S.assigned));
            setAssignments(await api.tierAssignments(selectedTier.networkTierId));
          }}
        />
      )}

      {mayWrite && selectedTier && (
        <RenameTier api={api} tier={selectedTier} onSaved={async () => { setAnnounce(t(S.saved)); await load(); }} />
      )}

      <ConfirmAction
        open={revoking !== null}
        onOpenChange={(o) => !o && setRevoking(null)}
        destructive
        title={S.revokeTitle}
        description={S.revokeReversible}
        // The reference the row shows, so the dialog names the same thing the operator clicked beside.
        body={fillLocalized(S.revokeBody, revoking?.scopeRef.slice(0, 8) ?? "")}
        confirmLabel={S.revoke}
        onConfirm={async () => {
          if (!revoking || !selected) return;
          try {
            await api.revokeAssignment(revoking.assignmentId);
            setAnnounce(t(S.revoked));
            setAssignments(await api.tierAssignments(selected));
          } catch (e) {
            setError(readErrorMessage(e));
          } finally {
            setRevoking(null);
          }
        }}
      />


      <ResolveAtDate api={api} />
    </div>
  );
}

function ResolveAtDate({ api }: { api: PolicyApi }) {
  const t = useLoc();
  const [providerId, setProviderId] = useState("");
  const [locationId, setLocationId] = useState("");
  const [serviceDate, setServiceDate] = useState(new Date().toISOString().slice(0, 10));
  const [result, setResult] = useState<TierResolutionView | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  return (
    <Card data-testid="tier-resolve">
      <h2 className="panel-h">{t(S.resolve)}</h2>
      <InputField label={t(S.providerId)} value={providerId} onChange={(e) => setProviderId(e.target.value)} />
      <InputField label={t(S.locationId)} value={locationId} onChange={(e) => setLocationId(e.target.value)} />
      <InputField type="date" label={t(S.serviceDate)} value={serviceDate} onChange={(e) => setServiceDate(e.target.value)} />
      <Button
        variant="secondary"
        onClick={async () => {
          setError(null);
          try {
            setResult(await api.resolveTier(providerId, serviceDate, locationId || undefined));
          } catch (e) {
            setError(readErrorMessage(e));
            setResult(null);
          }
        }}
      >
        {t(S.run)}
      </Button>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {result && (
        // Not `KpiList`, and deliberately: a status chip and a vocabulary term ("DefaultOutOfNetwork") are
        // not figures, and the KPI treatment sets its value at 34px tabular numerals because a KPI is a
        // number. `.pol-facts` is the same rule this always used, renamed to say what it holds.
        // `aria-live` stays on the list: the resolver replaces this block in place when Run is pressed, and
        // without it the only feedback is text quietly changing somewhere down the page.
        <dl className="pol-facts" aria-live="polite">
          <div>
            <dt>{t(S.resolvedTo)}</dt>
            <dd>
              <StatusChip kind={result.isOutOfNetwork ? "warn" : "ok"} label={result.tierCode} />
            </dd>
          </div>
          <div>
            <dt>{t(S.basis)}</dt>
            <dd>{result.basis}</dd>
          </div>
        </dl>
      )}
    </Card>
  );
}

/**
 * Put a provider — or one of its locations, or one contract service line — into this tier.
 *
 * <p>The provider is PICKED, not typed. The resolver above takes a raw uuid because it is a read: getting it
 * wrong wastes a lookup. Getting it wrong here reprices somebody's claims from a date, which is the same
 * consequence the revoke dialog treats as worth a confirmation.</p>
 *
 * <p>A retired tier is refused here rather than at the server: it returns
 * <code>409 TIER_RETIRED</code>, and offering the form so the operator can be told no is worse than saying
 * why up front — retired tiers stay listed on purpose, because claims priced against them must still
 * render.</p>
 */
function AssignToTier({
  api,
  tier,
  onAssigned,
}: {
  api: PolicyApi;
  tier: NetworkTierView;
  onAssigned: () => Promise<void>;
}) {
  const t = useLoc();
  const [providers, setProviders] = useState<TierProviderOption[]>([]);
  const [providerId, setProviderId] = useState("");
  const [scope, setScope] = useState<AssignmentScope>("Provider");
  const [scopeRef, setScopeRef] = useState("");
  const [from, setFrom] = useState(new Date().toISOString().slice(0, 10));
  const [until, setUntil] = useState("");
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [assignKey, rotateAssignKey] = useIdempotencyKey();

  useEffect(() => {
    let live = true;
    api.tierProviders()
      .then((rows) => live && setProviders(rows))
      // Degrades to an empty picker rather than taking the panel down: the tier list beside it is still
      // useful, and a directory outage is not a reason to hide the tier map.
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  const retired = tier.status !== "Active";

  async function submit() {
    setError(null);
    if (providerId === "") { setError(S.assignNeedsProvider); return; }
    // The scope ref for a provider-wide assignment IS the provider; for the other two it is an id the
    // operator brings from the contract, and there is nothing sensible to default it to.
    const ref = scope === "Provider" ? providerId : scopeRef.trim();
    if (ref === "") { setError(S.assignNeedsRef); return; }
    setBusy(true);
    try {
      await api.assignTier(
        tier.networkTierId,
        { scope, scopeRef: ref, effectiveFrom: from, effectiveTo: until || null },
        assignKey,
      );
      rotateAssignKey();
      setScopeRef("");
      setUntil("");
      await onAssigned();
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card data-testid="tier-assign">
      <h2 className="panel-h">{t(S.assign)}</h2>
      {retired ? (
        <InlineAlert tone="info">{t(S.retiredNoAssign)}</InlineAlert>
      ) : (
        <>
          <InlineAlert tone="info">{t(S.assignHint)}</InlineAlert>
          <ComboboxField
            label={t(S.provider)}
            placeholder={t(S.pickProvider)}
            value={providerId || null}
            onChange={setProviderId}
            options={providers.map((p) => ({ value: p.providerId, label: `${p.legalName} · ${p.providerCode}` }))}
          />
          <ComboboxField
            label={t(S.scopeLevel)}
            value={scope}
            onChange={(v: string) => setScope(v as AssignmentScope)}
            options={ASSIGNMENT_SCOPES.map((x) => ({ value: x.value, label: t(x.label()) }))}
          />
          {scope !== "Provider" && (
            <InputField
              label={t(S.reference)}
              help={t(S.referenceHint)}
              value={scopeRef}
              onChange={(e) => setScopeRef(e.target.value)}
            />
          )}
          <InputField type="date" label={t(S.from)} value={from} onChange={(e) => setFrom(e.target.value)} />
          <InputField type="date" label={t(S.until)} value={until} onChange={(e) => setUntil(e.target.value)} />
          <div aria-live="polite">
            {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
          </div>
          {/* No glyph. `plus` means "create a thing" and this creates a LINK between two things that
              already exist — the icon policy's own rule (button-icon-policy.test.ts) is that a glyph either
              means the action or is absent. */}
          <Button variant="primary" loading={busy} onClick={() => void submit()}>
            {t(S.assign)}
          </Button>
        </>
      )}
    </Card>
  );
}

/**
 * Correct what a tier is CALLED.
 *
 * <p>`updateTier` was implemented in policyApi and called by nothing, so a tier created with a typo in its
 * Arabic name was a tier that kept it — and the only remedy on offer was retiring it and creating another,
 * which the create panel's own note recommends for the code and the out-of-network flag. That advice is
 * right for those two, because priced claims refer to them. It is not right for a name.</p>
 */
function RenameTier({
  api,
  tier,
  onSaved,
}: {
  api: PolicyApi;
  tier: NetworkTierView;
  onSaved: () => Promise<void>;
}) {
  const t = useLoc();
  const [nameEn, setNameEn] = useState(tier.nameEn);
  const [nameAr, setNameAr] = useState(tier.nameAr);
  const [rank, setRank] = useState(String(tier.rank));
  const [description, setDescription] = useState(tier.description ?? "");
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);

  // Re-seed when the operator selects a different tier — without this the form keeps the previous tier's
  // name and saving it would rename the newly-selected one to the old one's label.
  useEffect(() => {
    setNameEn(tier.nameEn);
    setNameAr(tier.nameAr);
    setRank(String(tier.rank));
    setDescription(tier.description ?? "");
    setError(null);
  }, [tier]);

  return (
    <Card data-testid="tier-edit">
      <h2 className="panel-h">{t(S.edit)}</h2>
      <InlineAlert tone="info">{t(S.editHint)}</InlineAlert>
      <InputField label={t(S.nameEn)} value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
      <InputField label={t(S.nameAr)} value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
      <InputField label={t(S.rank)} value={rank} inputMode="numeric" onChange={(e) => setRank(e.target.value)} />
      <InputField label={t(S.description)} value={description} onChange={(e) => setDescription(e.target.value)} />
      <div aria-live="polite">
        {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      </div>
      <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={busy} onClick={async () => {
        setError(null);
        setBusy(true);
        try {
          await api.updateTier(tier.networkTierId, {
            nameEn, nameAr, rank: Number(rank || "1"), description: description.trim() || null,
          });
          await onSaved();
        } catch (e) {
          setError(writeErrorMessage(e).message);
        } finally {
          setBusy(false);
        }
      }}>
        {t(S.save)}
      </Button>
    </Card>
  );
}
