import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, Card, DataTable, DataTableView, Icon, InlineAlert, InputField, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type { NetworkTierView, PolicyApi, TierAssignmentView, TierResolutionView } from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerTiers } from "../authz/permissions";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";
import { ConfirmAction } from "./ConfirmAction";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";

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
} satisfies Record<string, Localized>;

export function NetworkTiers({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const { session } = useAuth();
  const mayWrite = mayAdministerTiers(session?.role);

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
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
            {
              key: "act",
              header: "",
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
  ], [t, fmt, mayWrite]);

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
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
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
          <label className="pol-check">
            <input type="checkbox" checked={isOon} onChange={(e) => setIsOon(e.target.checked)} />
            {t(S.oon)}
          </label>
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

      <ConfirmAction
        open={revoking !== null}
        onOpenChange={(o) => !o && setRevoking(null)}
        destructive
        title={S.revokeTitle}
        description={S.revokeReversible}
        // The reference the row shows, so the dialog names the same thing the operator clicked beside.
        body={{
          en: S.revokeBody.en.replace("{0}", revoking?.scopeRef.slice(0, 8) ?? ""),
          ar: S.revokeBody.ar.replace("{0}", revoking?.scopeRef.slice(0, 8) ?? ""),
        }}
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
