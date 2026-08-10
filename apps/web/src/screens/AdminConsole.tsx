import { useMemo, useState } from "react";
import {
  Button,
  Card,
  ComboboxField,
  DataTableView,
  Icon,
  InlineAlert,
  InputField,
  Modal,
  StatusChip,
  useTableQuery,
} from "@mersal/design-system";
import { useFormat } from "../i18n/useFormat";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type {
  AccessReviewCampaign,
  BreakGlassGrant,
  ConfigValueType,
  Localized,
  MasterDataVersion,
  SystemConfigEntry,
  TenantSummary,
} from "@mersal/contracts";
import { CONFIG_VALUE_TYPES, canonicaliseConfigValue } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite } from "../api/useWrite";
import { AsyncSection, PageHeader, tenantLabel, useLoc, useTenantNames } from "./_shared";

/*
 * ============================================================================================================
 * 28.10 — WHAT LEFT THIS FILE, AND WHY
 * ============================================================================================================
 * `AdminUsers` and `AdminPolicies` used to live here. 28.8 merged the people surface into "Users & Access"
 * and 28.9 turned the role→scope matrix into the Access Catalogue, and both of those replaced these two —
 * but neither deleted them. Nothing in `registry.tsx` had referenced either export since; they were two
 * screens' worth of code that compiled, type-checked, passed every guard, and could not be reached.
 *
 * That is worse than clutter. A dead screen looks exactly like a live one to the next person reading the
 * file, so it gets maintained: the 2FA column here would have drifted against the one in `AccessAdmin.tsx`
 * that people actually see, and the divergence would only surface when somebody fixed the wrong one.
 */

const S = {
  status: { en: "Status", ar: "الحالة" },
  search: { en: "Search", ar: "بحث" },
  noMatches: {
    en: "No rows match. Change the search or clear the filters.",
    ar: "لا توجد صفوف مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },

  tenantsTitle: { en: "Tenants", ar: "المستأجرون" },
  tenantsEmpty: { en: "No tenants registered.", ar: "لا يوجد مستأجرون." },
  tenantsSearchHint: { en: "Organisation name", ar: "اسم المؤسسة" },
  tenant: { en: "Organisation", ar: "المؤسسة" },
  created: { en: "Created", ar: "أُنشئ" },

  govTitle: { en: "Audit & Access Reviews", ar: "التدقيق والمراجعات" },
  campaigns: { en: "Access-review campaigns", ar: "حملات مراجعة الوصول" },
  campaignsEmpty: { en: "No access-review campaigns.", ar: "لا توجد حملات مراجعة." },
  campaign: { en: "Campaign", ar: "الحملة" },
  minTier: { en: "Min tier", ar: "أدنى فئة" },
  due: { en: "Due", ar: "الاستحقاق" },
  breakGlass: { en: "Break-glass grants", ar: "منح الوصول الطارئ" },
  breakGlassEmpty: { en: "No break-glass grants.", ar: "لا توجد منح طارئة." },
  requester: { en: "Requester", ar: "الطالب" },
  requesterNote: {
    en: "Requesters appear as governance tokens, not names — this dashboard records emergency access, and pairing a name with each one would make it a directory of who reached what.",
    ar: "يظهر الطالبون كرموز حوكمة لا كأسماء — فهذه اللوحة تسجّل الوصول الطارئ، وإقران اسم بكل رمز يحوّلها إلى دليل بمن وصل إلى ماذا.",
  },
  reasonCode: { en: "Reason", ar: "السبب" },
  requested: { en: "Requested", ar: "وقت الطلب" },
  expires: { en: "Expires", ar: "ينتهي" },

  mdTitle: { en: "Master Data", ar: "البيانات المرجعية" },
  mdEmpty: { en: "No master-data versions in force.", ar: "لا توجد إصدارات بيانات مرجعية فعّالة." },
  mdSearchHint: { en: "Code, system or rationale", ar: "الرمز أو النظام أو المبرر" },
  /*
    Stated, because a screen with no way to change anything reads as unfinished rather than as deliberate.
    Editing master data is `AdminPolicies.EditMasterData`, held by the Medical Director and the platform
    administrator — the argument being that clinical governance absorbs the consequence of a mis-mapped code,
    since it misroutes a diagnosis into their own approval queue. An org admin reads it and does not own it.
  */
  mdReadOnly: {
    en: "Read-only here. Clinical vocabularies are versioned by clinical governance, under Master Lists in the Medical Director portal.",
    ar: "للاطلاع فقط هنا. تُدار إصدارات المفردات السريرية عبر الحوكمة السريرية، ضمن القوائم المرجعية في بوابة المدير الطبي.",
  },
  system: { en: "System", ar: "النظام" },
  code: { en: "Code", ar: "الرمز" },
  version: { en: "Version", ar: "الإصدار" },
  effective: { en: "Effective from", ar: "ساري من" },
  retired: { en: "Retired", ar: "متقاعد" },
  inForce: { en: "In force", ar: "ساري" },
  rationale: { en: "Why", ar: "المبرر" },

  cfgTitle: { en: "System Config", ar: "إعدادات النظام" },
  cfgEmpty: { en: "No configuration entries.", ar: "لا توجد إعدادات." },
  cfgLede: {
    en: "The values that decide how the platform behaves for your organisation. Every change is versioned and audited — the previous value is retained, never overwritten.",
    ar: "القيم التي تحدد سلوك المنصة لمؤسستك. كل تغيير يُؤرشف ويُدقّق — تُحفظ القيمة السابقة ولا تُستبدل.",
  },
  cfgSearchHint: { en: "Setting name or value", ar: "اسم الإعداد أو قيمته" },
  scope: { en: "Applies to", ar: "ينطبق على" },
  key: { en: "Setting", ar: "الإعداد" },
  type: { en: "Type", ar: "النوع" },
  value: { en: "Value", ar: "القيمة" },
  platform: { en: "Every organisation", ar: "كل المؤسسات" },
  thisOrg: { en: "This organisation", ar: "هذه المؤسسة" },

  addSetting: { en: "Add a setting", ar: "إضافة إعداد" },
  changeValue: { en: "Change", ar: "تعديل" },
  editTitle: { en: "Change this setting", ar: "تعديل هذا الإعداد" },
  addTitle: { en: "Add a setting", ar: "إضافة إعداد" },
  editHelp: {
    en: "Saving records a new version. The value in force changes immediately for everyone in this organisation.",
    ar: "الحفظ يسجّل إصدارًا جديدًا. تتغيّر القيمة السارية فورًا لكل من في هذه المؤسسة.",
  },
  keyHelp: {
    en: "The name the platform reads, e.g. approvals.sla_hours. It cannot be changed afterwards — add a new setting instead.",
    ar: "الاسم الذي تقرأه المنصة، مثل approvals.sla_hours. لا يمكن تغييره لاحقًا — أضف إعدادًا جديدًا بدلًا من ذلك.",
  },
  keyRequired: { en: "A setting name is required.", ar: "اسم الإعداد مطلوب." },
  typeHelp: {
    en: "How the value is read. Choose it once, when the setting is created.",
    ar: "كيف تُقرأ القيمة. يُختار مرة واحدة عند إنشاء الإعداد.",
  },
  currently: { en: "Currently", ar: "القيمة الحالية" },
  versionShort: { en: "Version {n}", ar: "الإصدار {n}" },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  saved: { en: "Saved. The new value is in force.", ar: "تم الحفظ. القيمة الجديدة سارية الآن." },
  willStore: { en: "Stored as {v}", ar: "ستُحفظ بالشكل {v}" },

  // The value-type help, in the terms an administrator is actually deciding between. `Duration` gets the
  // longest note because it is the one that silently means something else: .NET reads a bare number as DAYS,
  // so a session timeout typed as "15" is a fortnight rather than a quarter of an hour.
  typeText: { en: "Text — any words", ar: "نص — أي كلمات" },
  typeWhole: { en: "Whole number — e.g. 24", ar: "عدد صحيح — مثل 24" },
  typeNumber: { en: "Decimal — e.g. 1.5", ar: "عدد عشري — مثل 1.5" },
  typeBoolean: { en: "Yes / no — true or false", ar: "نعم / لا — true أو false" },
  typeDuration: { en: "Duration — d.hh:mm:ss, e.g. 0.00:15:00", ar: "مدة — d.hh:mm:ss، مثل 0.00:15:00" },
  invalidText: { en: "Enter a value.", ar: "أدخل قيمة." },
  invalidWhole: { en: "Enter a whole number, e.g. 24.", ar: "أدخل عددًا صحيحًا، مثل 24." },
  invalidNumber: { en: "Enter a number, e.g. 1.5.", ar: "أدخل رقمًا، مثل 1.5." },
  invalidBoolean: { en: "Enter true or false.", ar: "أدخل true أو false." },
  invalidDuration: {
    en: "Enter d.hh:mm:ss — 0.00:15:00 is fifteen minutes. A bare number means DAYS.",
    ar: "أدخل d.hh:mm:ss — القيمة 0.00:15:00 تعني خمس عشرة دقيقة. الرقم المجرّد يعني أيامًا.",
  },
} satisfies Record<string, Localized>;

// 18.D2 (U7): see useFormat — Africa/Cairo + the app locale, never the browser's.

/** Tenants — the tenant registry. Super Admin only; the org-admin portal no longer links here. */
export function AdminTenants() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<TenantSummary[]>(() => api.adminTenants(), []);

  /*
    The `id` column is gone. It rendered a bare uuid in a `.tnum` span beside the name it identifies — which
    tells a reader nothing they cannot get from the name, cannot be typed anywhere, and is the widest column
    on the screen. The name IS the identity here; the uuid is a join key that leaked into a page.
  */
  const cols: Column<TenantSummary>[] = [
    { key: "name", header: t(S.tenant), cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "created", header: t(S.created), cell: (r) => <span className="tnum">{r.createdAt ? fmt.date(r.createdAt) : "—"}</span>, sortable: true, sortValue: (r) => r.createdAt ?? "" },
  ];

  const query = useTableQuery<TenantSummary>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => r.name,
    searchLabel: t(S.search),
    searchPlaceholder: t(S.tenantsSearchHint),
    pageSize: 25,
    initialSortKey: "name",
    persistKey: "admin-tenants",
  });

  return (
    <>
      <PageHeader title={t(S.tenantsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.tenantsEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.tenantsTitle)}
              emptyLabel={t(S.tenantsEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Audit & access reviews — recertification campaigns + the break-glass governance dashboard. */
export function AdminGovernance() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const campaigns = useAsync<AccessReviewCampaign[]>(() => api.accessReviewCampaigns(), []);
  const grants = useAsync<BreakGlassGrant[]>(() => api.breakGlassGrants(), []);

  const campCols: Column<AccessReviewCampaign>[] = [
    { key: "name", header: t(S.campaign), cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
    { key: "minTier", header: t(S.minTier), cell: (r) => <StatusChip kind="neu" label={r.minTier ?? "—"} /> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "due", header: t(S.due), cell: (r) => <span className="tnum">{r.dueAt ? fmt.date(r.dueAt) : "—"}</span>, sortable: true, sortValue: (r) => r.dueAt ?? "" },
  ];
  const bgCols: Column<BreakGlassGrant>[] = [
    // A governance TOKEN, and it stays one — see `requesterNote` under the heading. This is the one place in
    // the portal where an opaque identifier is the right answer rather than a leak, so it is explained on
    // screen instead of being left for the reader to mistake for an unresolved id.
    { key: "requester", header: t(S.requester), cell: (r) => <span className="tnum">{r.requesterToken}</span>, sortable: true, sortValue: (r) => r.requesterToken },
    { key: "reasonCode", header: t(S.reasonCode), cell: (r) => r.reasonCode, sortable: true, sortValue: (r) => r.reasonCode },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "requested", header: t(S.requested), cell: (r) => <span className="tnum">{fmt.date(r.requestedAt)}</span>, sortable: true, sortValue: (r) => r.requestedAt },
    { key: "expires", header: t(S.expires), cell: (r) => <span className="tnum">{r.expiresAt ? fmt.date(r.expiresAt) : "—"}</span>, sortable: true, sortValue: (r) => r.expiresAt ?? "" },
  ];

  const campQuery = useTableQuery<AccessReviewCampaign>({
    rows: campaigns.data ?? [],
    columns: campCols,
    searchText: (r) => [r.name, r.minTier ?? ""].join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.campaign),
    pageSize: 25,
    // Soonest first — this table is opened to find what is falling due, not to browse it.
    initialSortKey: "due",
    persistKey: "admin-campaigns",
  });
  const bgQuery = useTableQuery<BreakGlassGrant>({
    rows: grants.data ?? [],
    columns: bgCols,
    searchText: (r) => [r.requesterToken, r.reasonCode].join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.reasonCode),
    pageSize: 25,
    initialSortKey: "requested",
    persistKey: "admin-breakglass",
  });

  return (
    <>
      <PageHeader title={t(S.govTitle)} />
      <div className="stack" style={{ gap: "var(--sp4)" }}>
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          {/* `panel-h`, matching every other card title in the portal. These two used `section-h` — the
              11.5px uppercase eyebrow — so the same level of heading was rendered two different sizes on
              adjacent admin screens. */}
          <h2 className="panel-h">{t(S.campaigns)}</h2>
          <AsyncSection state={campaigns} isEmpty={(d) => d.length === 0} emptyLabel={S.campaignsEmpty}>
            {() => (
              <DataTableView
                query={campQuery}
                columns={campCols}
                rowKey={(r) => r.id}
                caption={t(S.campaigns)}
                emptyLabel={t(S.campaignsEmpty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <h2 className="panel-h">{t(S.breakGlass)}</h2>
          <p className="lede">{t(S.requesterNote)}</p>
          <AsyncSection state={grants} isEmpty={(d) => d.length === 0} emptyLabel={S.breakGlassEmpty}>
            {() => (
              <DataTableView
                query={bgQuery}
                columns={bgCols}
                rowKey={(r) => r.id}
                caption={t(S.breakGlass)}
                emptyLabel={t(S.breakGlassEmpty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
      </div>
    </>
  );
}

/** Master data — the effective-dated code-system versions currently in force (governance read, FR-MDM-007). */
export function AdminMasterData() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<MasterDataVersion[]>(() => api.adminMasterData(), []);

  const cols: Column<MasterDataVersion>[] = [
    { key: "system", header: t(S.system), cell: (r) => <span className="tnum">{r.system}</span>, sortable: true, sortValue: (r) => r.system },
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    // `numeric` on the COLUMN, not `.tnum` on the value — the class sets the figure width and leaves the
    // column ragged, so a table of version numbers does not line up (table-numeric-columns guards this).
    { key: "version", header: t(S.version), cell: (r) => r.versionNo, numeric: true, sortable: true, sortValue: (r) => r.versionNo },
    {
      // Was `label={"—"}` on the in-force path: a status column whose good state is a dash makes the reader
      // decide whether the dash means "not retired" or "unknown". It says which.
      key: "retired",
      header: t(S.status),
      cell: (r) => <StatusChip kind={r.retired ? "warn" : "ok"} label={t(r.retired ? S.retired : S.inForce)} />,
      sortable: true,
      sortValue: (r) => Number(r.retired),
    },
    { key: "effective", header: t(S.effective), cell: (r) => <span className="tnum">{fmt.date(r.effectiveFrom)}</span>, sortable: true, sortValue: (r) => r.effectiveFrom },
    { key: "rationale", header: t(S.rationale), cell: (r) => r.rationale ?? "—" },
  ];

  const filters: TableFilterSpec<MasterDataVersion>[] = useMemo(() => [
    {
      key: "system",
      label: t(S.system),
      options: [...new Set((state.data ?? []).map((v) => v.system))].sort().map((v) => ({ value: v, label: v })),
      match: (r, value) => r.system === value,
    },
  ], [t, state.data]);

  /*
    The server returns up to FIVE HUNDRED rows here and this rendered as one bare `DataTable` — no search, no
    filter, no paging. Finding one ICD code meant scrolling a five-hundred-row table, which is the same as
    not having the screen.
  */
  const query = useTableQuery<MasterDataVersion>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => [r.system, r.code, r.rationale ?? ""].join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.mdSearchHint),
    filters,
    pageSize: 25,
    initialSortKey: "code",
    persistKey: "admin-masterdata",
  });

  return (
    <>
      <PageHeader title={t(S.mdTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="lede">{t(S.mdReadOnly)}</p>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.mdEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.mdTitle)}
              emptyLabel={t(S.mdEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/**
 * System config — the typed, effective-dated configuration entries in force, and the editor for them.
 *
 * ==========================================================================================================
 * 28.10 — THE ENDPOINT WAS ALWAYS THERE
 * ==========================================================================================================
 * `PUT /api/v1/admin/system-config` has existed since 8b.2: typed, validated, effective-dated, audited,
 * gated on `AdminPolicies.EditConfig` — which org_admin holds. Nothing in the SPA has ever called it. So
 * every one of these values was, from the product's point of view, a hardcoded constant: visible, and
 * changeable only by someone with a psql prompt.
 *
 * That is the shape most "hardcoded values" take in a system this size. Not a literal in the source — a row
 * in a table nobody was given a way to reach.
 */
export function AdminConfig() {
  const api = useApi();
  const t = useLoc();
  const names = useTenantNames();
  const [reloadKey, setReloadKey] = useState(0);
  const [editing, setEditing] = useState<SystemConfigEntry | null>(null);
  const [adding, setAdding] = useState(false);
  const [saved, setSaved] = useState(false);
  const state = useAsync<SystemConfigEntry[]>(() => api.adminSystemConfig(), [reloadKey]);

  const cols: Column<SystemConfigEntry>[] = [
    {
      /*
        Was `r.tenantId.slice(0, 8)` — the first eight characters of a uuid. Worse than the whole thing: it
        cannot be copied into anything, it is not guaranteed unique, and it reads as a truncation bug. It is
        now a NAME when the caller may read the tenant registry, and an honest description of the scope when
        they may not.
      */
      key: "scope",
      header: t(S.scope),
      cell: (r) =>
        r.tenantId === "*" ? (
          <StatusChip kind="info" label={t(S.platform)} />
        ) : (
          <span>{tenantLabel(r.tenantId, names, t(S.thisOrg))}</span>
        ),
      sortable: true,
      sortValue: (r) => (r.tenantId === "*" ? "" : tenantLabel(r.tenantId, names, t(S.thisOrg))),
    },
    { key: "key", header: t(S.key), cell: (r) => <code className="scope-key">{r.key}</code>, sortable: true, sortValue: (r) => r.key },
    { key: "type", header: t(S.type), cell: (r) => <StatusChip kind="neu" label={r.type} />, sortable: true, sortValue: (r) => r.type },
    { key: "value", header: t(S.value), cell: (r) => <span className="tnum">{r.value}</span>, sortable: true, sortValue: (r) => r.value },
    { key: "version", header: t(S.version), cell: (r) => r.versionNo, numeric: true, sortable: true, sortValue: (r) => r.versionNo },
    {
      key: "edit",
      // Named rather than blank. An empty `<th>` is announced as a column with no name, and the reader of a
      // row has nothing to tell them what the button at the end of it will act on.
      header: t(S.changeValue),
      cell: (r) => (
        <Button variant="ghost" size="sm" leadingIcon={<Icon name="pen" />} onClick={() => setEditing(r)}>
          {t(S.changeValue)}
        </Button>
      ),
    },
  ];

  const query = useTableQuery<SystemConfigEntry>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => [r.key, r.value, r.type].join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.cfgSearchHint),
    pageSize: 25,
    initialSortKey: "key",
    persistKey: "admin-config",
  });

  const done = () => {
    setEditing(null);
    setAdding(false);
    setSaved(true);
    setReloadKey((k) => k + 1);
  };

  return (
    <>
      <PageHeader
        title={t(S.cfgTitle)}
        actions={
          <Button variant="primary" leadingIcon={<Icon name="plus" />} onClick={() => { setSaved(false); setAdding(true); }}>
            {t(S.addSetting)}
          </Button>
        }
      />
      <p className="lede">{t(S.cfgLede)}</p>
      {/* Announced, not merely rendered: a successful save changes one cell of one row in a paged table, and
          an outcome nobody is told about reads as a button that did nothing. */}
      <div aria-live="polite">{saved && <InlineAlert tone="ok">{t(S.saved)}</InlineAlert>}</div>
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.cfgEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.cfgTitle)}
              emptyLabel={t(S.cfgEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>

      <ConfigDialog
        open={editing !== null || adding}
        entry={editing}
        onClose={() => { setEditing(null); setAdding(false); }}
        onSaved={done}
      />
    </>
  );
}

/** The per-type "what does a good value look like" note, and the message when it is not one. */
const TYPE_HELP: Record<ConfigValueType, { hint: Localized; error: Localized }> = {
  Text: { hint: S.typeText, error: S.invalidText },
  Whole: { hint: S.typeWhole, error: S.invalidWhole },
  Number: { hint: S.typeNumber, error: S.invalidNumber },
  Boolean: { hint: S.typeBoolean, error: S.invalidBoolean },
  Duration: { hint: S.typeDuration, error: S.invalidDuration },
};

/**
 * Add a setting, or change one.
 *
 * <p>One dialog for both, because they are one act. The difference is only that an existing setting's KEY
 * and TYPE are fixed: changing either would silently create a second setting or reinterpret a stored value,
 * neither of which is what "change this" means. So they are shown, and not editable.</p>
 */
function ConfigDialog({
  open,
  entry,
  onClose,
  onSaved,
}: {
  open: boolean;
  entry: SystemConfigEntry | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const api = useApi();
  const write = useWrite();
  const [key, setKey] = useState("");
  const [type, setType] = useState<ConfigValueType>("Text");
  const [value, setValue] = useState("");
  const [touched, setTouched] = useState(false);
  const [seeded, setSeeded] = useState<string | null>(null);

  // Seeded once per opening rather than from an effect, which would overwrite what the administrator has
  // typed on every re-render of the table behind it.
  const seedKey = open ? (entry?.id ?? "__new__") : null;
  if (seedKey !== seeded) {
    setSeeded(seedKey);
    setKey(entry?.key ?? "");
    setType((entry && (CONFIG_VALUE_TYPES as readonly string[]).includes(entry.type) ? entry.type : "Text") as ConfigValueType);
    setValue(entry?.value ?? "");
    setTouched(false);
    write.reset();
  }

  const editing = entry !== null;
  const keyOk = key.trim().length > 0;
  // The canonical form the server would store, or null when the value does not parse as the chosen type.
  const canonical = canonicaliseConfigValue(type, value);
  const valueOk = canonical !== null;

  async function submit() {
    setTouched(true);
    if (!keyOk || !valueOk) return;
    const ok = await write.run(() =>
      api.adminSystemConfigSet({
        key: key.trim(),
        type,
        value: value.trim(),
        // The row's own scope on an edit; the caller's own tenant on a create. Never "*" from here — an org
        // admin naming the platform scope is a 403 (`cross-tenant-denied`), so offering it would be offering
        // a control that fails for the people most likely to press it.
        tenantId: entry?.tenantId,
      }),
    );
    if (ok) onSaved();
  }

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={editing ? `${t(S.editTitle)} — ${entry.key}` : t(S.addTitle)}
      description={t(S.editHelp)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t(S.cancel)}
          </Button>
          <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={write.busy} onClick={() => void submit()}>
            {t(S.save)}
          </Button>
        </>
      }
    >
      <div className="stack-3">
        {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}

        {editing ? (
          <p className="muted" style={{ margin: 0 }}>
            {t(S.currently)}: <span className="tnum">{entry.value}</span> ·{" "}
            {t(S.versionShort).replace("{n}", String(entry.versionNo))} · {entry.type}
          </p>
        ) : (
          <>
            <InputField
              label={t(S.key)}
              help={t(S.keyHelp)}
              value={key}
              error={touched && !keyOk ? t(S.keyRequired) : undefined}
              onChange={(e) => setKey(e.target.value)}
            />
            <ComboboxField
              id="config-type"
              label={t(S.type)}
              help={t(S.typeHelp)}
              value={type}
              onChange={(v) => setType(v as ConfigValueType)}
              options={CONFIG_VALUE_TYPES.map((x) => ({ value: x, label: x, hint: t(TYPE_HELP[x].hint) }))}
            />
          </>
        )}

        <InputField
          label={t(S.value)}
          help={t(TYPE_HELP[type].hint)}
          value={value}
          error={touched && !valueOk ? t(TYPE_HELP[type].error) : undefined}
          onChange={(e) => setValue(e.target.value)}
        />
        {/* Shown only when the stored form would DIFFER from what was typed — `TRUE` becoming `true`, `1.50`
            becoming `1.5`. Silence when they match; a note that always fires is a note nobody reads. */}
        {valueOk && canonical !== value.trim() && (
          <p className="muted" style={{ margin: 0 }}>{t(S.willStore).replace("{v}", canonical)}</p>
        )}
      </div>
    </Modal>
  );
}
