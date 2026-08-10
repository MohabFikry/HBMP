import { Card, DataTable, StatusChip } from "@mersal/design-system";
import { useFormat } from "../i18n/useFormat";
import type { Column } from "@mersal/design-system";
import type {
  AccessReviewCampaign,
  IdentityUser,
  RoleScopeGrant,
  BreakGlassGrant,
  Localized,
  MasterDataVersion,
  RoleBinding,
  SodConflict,
  SystemConfigEntry,
  TenantSummary,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  usersTitle: { en: "Users & Roles", ar: "المستخدمون والأدوار" },
  usersEmpty: { en: "No role bindings in this tenant.", ar: "لا توجد أدوار مُسندة في هذا المستأجر." },
  subject: { en: "Subject", ar: "المستخدم" },
  role: { en: "Role", ar: "الدور" },
  scope: { en: "Scope", ar: "النطاق" },
  tier: { en: "Sensitivity", ar: "الحساسية" },
  status: { en: "Status", ar: "الحالة" },
  granted: { en: "Granted", ar: "مُنح" },
  reviewDue: { en: "Review due", ar: "موعد المراجعة" },

  policiesTitle: { en: "Segregation of Duties", ar: "الفصل بين المهام" },
  policiesEmpty: { en: "No SoD conflict rules defined.", ar: "لا توجد قواعد تعارض." },
  roleA: { en: "Role", ar: "الدور" },
  roleB: { en: "Conflicts with", ar: "يتعارض مع" },
  reason: { en: "Reason", ar: "السبب" },

  tenantsTitle: { en: "Tenants", ar: "المستأجرون" },
  tenantsEmpty: { en: "No tenants registered.", ar: "لا يوجد مستأجرون." },
  tenant: { en: "Tenant", ar: "المستأجر" },
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
  reasonCode: { en: "Reason", ar: "السبب" },
  requested: { en: "Requested", ar: "وقت الطلب" },
  expires: { en: "Expires", ar: "ينتهي" },

  mdTitle: { en: "Master Data", ar: "البيانات المرجعية" },
  mdEmpty: { en: "No master-data versions in force.", ar: "لا توجد إصدارات بيانات مرجعية فعّالة." },
  system: { en: "System", ar: "النظام" },
  code: { en: "Code", ar: "الرمز" },
  version: { en: "Version", ar: "الإصدار" },
  effective: { en: "Effective from", ar: "ساري من" },
  retired: { en: "Retired", ar: "متقاعد" },
  cfgTitle: { en: "System Config", ar: "إعدادات النظام" },
  cfgEmpty: { en: "No configuration entries.", ar: "لا توجد إعدادات." },
  scope2: { en: "Scope", ar: "النطاق" },
  key: { en: "Key", ar: "المفتاح" },
  type: { en: "Type", ar: "النوع" },
  value: { en: "Value", ar: "القيمة" },
  platform: { en: "Platform", ar: "المنصة" },
  // 18.C2 (W5) — identity-store columns.
  username: { en: "Username", ar: "اسم المستخدم" },
  displayName: { en: "Name", ar: "الاسم" },
  twoFactor: { en: "Second factor", ar: "التحقق بخطوتين" },
  mfaOn: { en: "Enrolled", ar: "مُفعّل" },
  mfaOff: { en: "Not enrolled", ar: "غير مُفعّل" },
  active: { en: "Active", ar: "نشط" },
  deprovisioned: { en: "De-provisioned", ar: "مُعطّل" },
  accountsHeading: { en: "Accounts (identity store)", ar: "الحسابات (مخزن الهوية)" },
  bindingsHeading: { en: "Role bindings & recertification", ar: "ارتباطات الأدوار وإعادة الاعتماد" },
  sodHeading: { en: "Segregation-of-duties conflicts", ar: "تعارضات فصل المهام" },
  scopeHeading: { en: "Role → scope matrix (live)", ar: "مصفوفة الأدوار والصلاحيات (مباشرة)" },
  scopeNote: {
    en: "What a token issued to this role would actually carry — read from the issuer, not inferred.",
    ar: "ما يحمله فعليًا الرمز الصادر لهذا الدور — يُقرأ من جهة الإصدار.",
  },
  scopes: { en: "Scopes", ar: "الصلاحيات" },
  scopeCount: { en: "Count", ar: "العدد" },
} satisfies Record<string, Localized>;

// 18.D2 (U7): see useFormat — Africa/Cairo + the app locale, never the browser's.

/**
 * Users & roles.
 *
 * 18.C2 (audit R2 W5) — repointed at the IDENTITY STORE. This screen read admin-service's access-matrix
 * projection, which knows role bindings and nothing about the account behind them, so an administrator could
 * not see the two things they open this page to check: is the account still active, and does it carry a
 * second factor? Phase 17 moved users into identity-service and the console was never repointed, so it went
 * on rendering a view of a system that had stopped being the source of truth.
 *
 * The 2FA column is the one that matters. MFA gates every admin scope and every break-glass request on the
 * platform, and until now no screen anywhere showed whether a given account actually had one.
 *
 * Role BINDINGS (tier, recertification due) remain admin-service's — that is genuinely its data — and are
 * shown below the account list rather than in place of it.
 */
export function AdminUsers() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const users = useAsync<IdentityUser[]>(() => api.identityUsers(), []);
  const bindings = useAsync<RoleBinding[]>(() => api.accessMatrix(), []);

  const userCols: Column<IdentityUser>[] = [
    { key: "username", header: t(S.username), cell: (r) => <span>{r.username}</span>, sortable: true, sortValue: (r) => r.username },
    { key: "displayName", header: t(S.displayName), cell: (r) => r.displayName, sortable: true, sortValue: (r) => r.displayName },
    { key: "roles", header: t(S.role), cell: (r) => (r.roles.length ? r.roles.join(", ") : "—") },
    {
      key: "active",
      header: t(S.status),
      cell: (r) => <StatusChip kind={r.isActive ? "ok" : "neu"} label={t(r.isActive ? S.active : S.deprovisioned)} />, sortable: true, sortValue: (r) => Number(r.isActive) },
    {
      // Text label as well as chip kind: an administrator scans this column looking for the accounts that
      // CANNOT satisfy MFA, and colour alone would not carry that (21-accessibility).
      key: "twoFactor",
      header: t(S.twoFactor),
      cell: (r) => <StatusChip kind={r.twoFactorEnabled ? "ok" : "warn"} label={t(r.twoFactorEnabled ? S.mfaOn : S.mfaOff)} />,
    },
  ];

  const bindingCols: Column<RoleBinding>[] = [
    { key: "subject", header: t(S.subject), cell: (r) => <span className="tnum">{r.subjectToken}</span>, sortable: true, sortValue: (r) => r.subjectToken },
    { key: "role", header: t(S.role), cell: (r) => r.role, sortable: true, sortValue: (r) => r.role },
    { key: "scope", header: t(S.scope), cell: (r) => r.scope, sortable: true, sortValue: (r) => r.scope },
    { key: "tier", header: t(S.tier), cell: (r) => <StatusChip kind="neu" label={r.tier} />, sortable: true, sortValue: (r) => r.tier },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "reviewDue", header: t(S.reviewDue), cell: (r) => <span className="tnum">{fmt.date(r.reviewDueAt)}</span>, sortable: true, sortValue: (r) => r.reviewDueAt },
  ];

  return (
    <>
      <PageHeader title={t(S.usersTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <h2 className="panel-h">{t(S.accountsHeading)}</h2>
        <AsyncSection<IdentityUser[]> state={users} isEmpty={(d) => d.length === 0} emptyLabel={S.usersEmpty}>
          {(rows) => <DataTable columns={userCols} rows={rows} rowKey={(r) => r.id} caption={t(S.accountsHeading)} />}
        </AsyncSection>
      </Card>
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp3)" }}>
        <h2 className="panel-h">{t(S.bindingsHeading)}</h2>
        <AsyncSection<RoleBinding[]> state={bindings} isEmpty={(d) => d.length === 0} emptyLabel={S.usersEmpty}>
          {(rows) => <DataTable columns={bindingCols} rows={rows} rowKey={(r) => r.id} caption={t(S.bindingsHeading)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/**
 * Permissions / policies — the Segregation-of-Duties conflict matrix (10-role-matrix §7) and, since 18.C2
 * (W5), the live role→scope matrix from the identity store.
 *
 * The SoD matrix says which roles must not be held together. It does not say what a role can DO — and that is
 * what the issuer reads to build a token's `scope` claim. 18.B3 found 141 rule/role pairs where a policy rule
 * named a role that could not hold the scope the rule required: silent denials nobody could see, because a
 * missing grant produces a 403 and not an error. This table is where that becomes visible — it renders
 * `/identity/effective-scopes`, the exact seam the issuer uses, so what is on screen is what a token carries.
 */
export function AdminPolicies() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<SodConflict[]>(() => api.sodMatrix(), []);
  const scopes = useAsync<RoleScopeGrant[]>(() => api.identityRoleScopes(), []);
  const scopeCols: Column<RoleScopeGrant>[] = [
    { key: "role", header: t(S.role), cell: (r) => <StatusChip kind="neu" label={r.role} />, sortable: true, sortValue: (r) => r.role },
    { key: "count", header: t(S.scopeCount), cell: (r) => r.scopes.length, numeric: true, sortable: true, sortValue: (r) => r.scopes.length },
    { key: "scopes", header: t(S.scopes), cell: (r) => <span>{r.scopes.length ? r.scopes.join(" · ") : "—"}</span> },
  ];
  const cols: Column<SodConflict>[] = [
    { key: "roleA", header: t(S.roleA), cell: (r) => <StatusChip kind="info" label={r.roleA} />, sortable: true, sortValue: (r) => r.roleA },
    { key: "roleB", header: t(S.roleB), cell: (r) => <StatusChip kind="warn" label={r.roleB} />, sortable: true, sortValue: (r) => r.roleB },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason, sortable: true, sortValue: (r) => r.reason },
  ];
  return (
    <>
      <PageHeader title={t(S.policiesTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <h2 className="panel-h">{t(S.sodHeading)}</h2>
        <AsyncSection<SodConflict[]> state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.policiesEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => `${r.roleA}:${r.roleB}`} caption={t(S.sodHeading)} />}
        </AsyncSection>
      </Card>
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp3)" }}>
        <h2 className="panel-h">{t(S.scopeHeading)}</h2>
        <p className="muted" style={{ marginTop: 0 }}>{t(S.scopeNote)}</p>
        <AsyncSection<RoleScopeGrant[]> state={scopes} isEmpty={(d) => d.length === 0} emptyLabel={S.policiesEmpty}>
          {(rows) => <DataTable columns={scopeCols} rows={rows} rowKey={(r) => r.role} caption={t(S.scopeHeading)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Tenants / providers — the tenant registry (super-admin scope). */
export function AdminTenants() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<TenantSummary[]>(() => api.adminTenants(), []);
  const cols: Column<TenantSummary>[] = [
    { key: "name", header: t(S.tenant), cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
    { key: "id", header: "ID", cell: (r) => <span className="tnum muted">{r.id}</span>, sortable: true, sortValue: (r) => r.id },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "created", header: t(S.created), cell: (r) => <span className="tnum">{fmt.date(r.createdAt)}</span>, sortable: true, sortValue: (r) => r.createdAt },
  ];
  return (
    <>
      <PageHeader title={t(S.tenantsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.tenantsEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.tenantsTitle)} />}
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
    { key: "due", header: t(S.due), cell: (r) => <span className="tnum">{fmt.date(r.dueAt)}</span>, sortable: true, sortValue: (r) => r.dueAt },
  ];
  const bgCols: Column<BreakGlassGrant>[] = [
    { key: "requester", header: t(S.requester), cell: (r) => <span className="tnum">{r.requesterToken}</span>, sortable: true, sortValue: (r) => r.requesterToken },
    { key: "reasonCode", header: t(S.reasonCode), cell: (r) => r.reasonCode, sortable: true, sortValue: (r) => r.reasonCode },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "requested", header: t(S.requested), cell: (r) => <span className="tnum">{fmt.date(r.requestedAt)}</span>, sortable: true, sortValue: (r) => r.requestedAt },
    { key: "expires", header: t(S.expires), cell: (r) => <span className="tnum">{fmt.date(r.expiresAt)}</span>, sortable: true, sortValue: (r) => r.expiresAt },
  ];
  return (
    <>
      <PageHeader title={t(S.govTitle)} />
      <div className="stack" style={{ gap: "var(--sp4)" }}>
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.campaigns)}</h2>
          <AsyncSection state={campaigns} isEmpty={(d) => d.length === 0} emptyLabel={S.campaignsEmpty}>
            {(rows) => <DataTable columns={campCols} rows={rows} rowKey={(r) => r.id} caption={t(S.campaigns)} />}
          </AsyncSection>
        </Card>
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.breakGlass)}</h2>
          <AsyncSection state={grants} isEmpty={(d) => d.length === 0} emptyLabel={S.breakGlassEmpty}>
            {(rows) => <DataTable columns={bgCols} rows={rows} rowKey={(r) => r.id} caption={t(S.breakGlass)} />}
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
    { key: "version", header: t(S.version), cell: (r) => <span className="tnum">v{r.versionNo}</span> },
    { key: "retired", header: t(S.retired), cell: (r) => <StatusChip kind={r.retired ? "warn" : "ok"} label={r.retired ? t(S.retired) : "—"} />, sortable: true, sortValue: (r) => Number(r.retired) },
    { key: "effective", header: t(S.effective), cell: (r) => <span className="tnum">{fmt.date(r.effectiveFrom)}</span>, sortable: true, sortValue: (r) => r.effectiveFrom },
  ];
  return (
    <>
      <PageHeader title={t(S.mdTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.mdEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.mdTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** System config — the typed, effective-dated configuration entries in force (platform "*" or per-tenant). */
export function AdminConfig() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<SystemConfigEntry[]>(() => api.adminSystemConfig(), []);
  const cols: Column<SystemConfigEntry>[] = [
    { key: "scope", header: t(S.scope2), cell: (r) => (r.tenantId === "*" ? <StatusChip kind="info" label={t(S.platform)} /> : <span className="tnum muted">{r.tenantId.slice(0, 8)}</span>) },
    { key: "key", header: t(S.key), cell: (r) => <span className="tnum">{r.key}</span>, sortable: true, sortValue: (r) => r.key },
    { key: "type", header: t(S.type), cell: (r) => <StatusChip kind="neu" label={r.type} />, sortable: true, sortValue: (r) => r.type },
    { key: "value", header: t(S.value), cell: (r) => <span className="tnum">{r.value}</span>, sortable: true, sortValue: (r) => r.value },
    { key: "version", header: t(S.version), cell: (r) => <span className="tnum">v{r.versionNo}</span> },
  ];
  return (
    <>
      <PageHeader title={t(S.cfgTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.cfgEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.cfgTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}
