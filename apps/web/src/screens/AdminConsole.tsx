import { Card, DataTable, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  AccessReviewCampaign,
  BreakGlassGrant,
  Localized,
  RoleBinding,
  SodConflict,
  TenantSummary,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  usersTitle: { en: "Users & roles", ar: "المستخدمون والأدوار" },
  usersEmpty: { en: "No role bindings in this tenant.", ar: "لا توجد أدوار مُسندة في هذا المستأجر." },
  subject: { en: "Subject", ar: "المستخدم" },
  role: { en: "Role", ar: "الدور" },
  scope: { en: "Scope", ar: "النطاق" },
  tier: { en: "Sensitivity", ar: "الحساسية" },
  status: { en: "Status", ar: "الحالة" },
  granted: { en: "Granted", ar: "مُنح" },
  reviewDue: { en: "Review due", ar: "موعد المراجعة" },

  policiesTitle: { en: "Segregation of duties", ar: "الفصل بين المهام" },
  policiesEmpty: { en: "No SoD conflict rules defined.", ar: "لا توجد قواعد تعارض." },
  roleA: { en: "Role", ar: "الدور" },
  roleB: { en: "Conflicts with", ar: "يتعارض مع" },
  reason: { en: "Reason", ar: "السبب" },

  tenantsTitle: { en: "Tenants", ar: "المستأجرون" },
  tenantsEmpty: { en: "No tenants registered.", ar: "لا يوجد مستأجرون." },
  tenant: { en: "Tenant", ar: "المستأجر" },
  created: { en: "Created", ar: "أُنشئ" },

  govTitle: { en: "Audit & access reviews", ar: "التدقيق والمراجعات" },
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
} satisfies Record<string, Localized>;

const dt = (s?: string) => (s ? new Date(s).toLocaleDateString() : "—");

/** Users & roles — the access matrix (who holds which role, at what tier, and when it needs recertifying). */
export function AdminUsers() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<RoleBinding[]>(() => api.accessMatrix(), []);
  const cols: Column<RoleBinding>[] = [
    { key: "subject", header: t(S.subject), cell: (r) => <span className="tnum">{r.subjectToken}</span> },
    { key: "role", header: t(S.role), cell: (r) => r.role },
    { key: "scope", header: t(S.scope), cell: (r) => r.scope },
    { key: "tier", header: t(S.tier), cell: (r) => <StatusChip kind="neu" label={r.tier} /> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "reviewDue", header: t(S.reviewDue), cell: (r) => <span className="tnum">{dt(r.reviewDueAt)}</span> },
  ];
  return (
    <>
      <PageHeader title={t(S.usersTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.usersEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.usersTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Permissions / policies — the Segregation-of-Duties conflict matrix (10-role-matrix §7). */
export function AdminPolicies() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<SodConflict[]>(() => api.sodMatrix(), []);
  const cols: Column<SodConflict>[] = [
    { key: "roleA", header: t(S.roleA), cell: (r) => <StatusChip kind="info" label={r.roleA} /> },
    { key: "roleB", header: t(S.roleB), cell: (r) => <StatusChip kind="warn" label={r.roleB} /> },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason },
  ];
  return (
    <>
      <PageHeader title={t(S.policiesTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.policiesEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => `${r.roleA}:${r.roleB}`} caption={t(S.policiesTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Tenants / providers — the tenant registry (super-admin scope). */
export function AdminTenants() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<TenantSummary[]>(() => api.adminTenants(), []);
  const cols: Column<TenantSummary>[] = [
    { key: "name", header: t(S.tenant), cell: (r) => r.name },
    { key: "id", header: "ID", cell: (r) => <span className="tnum muted">{r.id}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "created", header: t(S.created), cell: (r) => <span className="tnum">{dt(r.createdAt)}</span> },
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
  const api = useApi();
  const t = useLoc();
  const campaigns = useAsync<AccessReviewCampaign[]>(() => api.accessReviewCampaigns(), []);
  const grants = useAsync<BreakGlassGrant[]>(() => api.breakGlassGrants(), []);

  const campCols: Column<AccessReviewCampaign>[] = [
    { key: "name", header: t(S.campaign), cell: (r) => r.name },
    { key: "minTier", header: t(S.minTier), cell: (r) => <StatusChip kind="neu" label={r.minTier ?? "—"} /> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "due", header: t(S.due), cell: (r) => <span className="tnum">{dt(r.dueAt)}</span> },
  ];
  const bgCols: Column<BreakGlassGrant>[] = [
    { key: "requester", header: t(S.requester), cell: (r) => <span className="tnum">{r.requesterToken}</span> },
    { key: "reasonCode", header: t(S.reasonCode), cell: (r) => r.reasonCode },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "requested", header: t(S.requested), cell: (r) => <span className="tnum">{dt(r.requestedAt)}</span> },
    { key: "expires", header: t(S.expires), cell: (r) => <span className="tnum">{dt(r.expiresAt)}</span> },
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
