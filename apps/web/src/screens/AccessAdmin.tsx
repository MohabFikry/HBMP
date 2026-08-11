import { useCallback, useMemo, useState } from "react";
import {
  Button,
  Card,
  ComboboxField,
  DataTable,
  Icon,
  InputField,
  InlineAlert,
  Modal,
  SearchField,
  StatusChip,
  Tabs,
  TextareaField,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  AccessSession,
  IdentityUser,
  BranchScopeGrant,
  BranchSummary,
  EffectiveAccess,
  Localized,
  MembershipDetail,
  MembershipOverride,
  MembershipRow,
  ScopeCatalogEntry,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite } from "../api/useWrite";
import { useFormat } from "../i18n/useFormat";
import { issuerRoleFor } from "../config";
import { PORTALS } from "../portals/catalog";
import { AsyncSection, PageHeader, tenantLabel, useActorNames, useLoc, useTenantNames } from "./_shared";
import { EffectiveAccessPreview } from "./EffectiveAccessPreview";
import {
  AccountStatusChip,
  CreateUserDialog,
  EditUserDialog,
  UserActionDialog,
  UserRowActions,
  USER_ADMIN_STRINGS,
} from "./UserAdmin";

/**
 * Phase 21.6 — the user & access administration screens (design 40 §1–§3, §6).
 *
 * THE MEMBERSHIP IS THE SUBJECT, not the user. Every screen here is keyed on a membership, because the same
 * person legitimately holds different authority in two organisations and a screen keyed on the identity
 * would have to blend them — which is the one thing invariant 1 forbids.
 *
 * UI GATING HERE IS COSMETIC (§6). Hiding an action a caller cannot use is a usability courtesy; every
 * action re-checks on the server, and `UiGatingIsCosmeticTests` hand-crafts the request behind each hidden
 * affordance and asserts the API refuses it. A test that asserted "the button is absent" would keep passing
 * after the endpoint turned permissive, and the person who reaches these endpoints is not using the UI.
 */

const S = {
  rosterTitle: { en: "Users & Access", ar: "المستخدمون والصلاحيات" },
  rosterTabsLabel: { en: "Users and access sections", ar: "أقسام المستخدمين والصلاحيات" },
  // 28.8 — the identity half and the authority half. See the comment at the tab strip for why they are two.
  tabAccounts: { en: "Accounts", ar: "الحسابات" },
  tabMemberships: { en: "Authority", ar: "الصلاحيات" },
  searchAccounts: { en: "Search by name, username or email", ar: "ابحث بالاسم أو اسم المستخدم أو البريد" },
  accountsEmpty: { en: "No accounts yet. Add one to get started.", ar: "لا توجد حسابات بعد. أضف حسابًا للبدء." },
  email: { en: "Email", ar: "البريد الإلكتروني" },
  noEmail: { en: "No address", ar: "بلا بريد" },
  position: { en: "Position", ar: "المسمى الوظيفي" },
  // Said rather than left blank. An empty cell reads as a rendering fault; "Not recorded" reads as a fact,
  // and it is the correct fact for a service account, which has no job title because it is not a person.
  noPosition: { en: "Not recorded", ar: "غير مسجّل" },
  portalsCol: { en: "Portals", ar: "البوابات" },
  twoFactor: { en: "Second factor", ar: "التحقق بخطوتين" },
  mfaOn: { en: "Enrolled", ar: "مُفعّل" },
  mfaOff: { en: "Not enrolled", ar: "غير مُفعّل" },
  rosterEmpty: { en: "No memberships in this tenant.", ar: "لا توجد عضويات في هذا المستأجر." },
  search: { en: "Search by name or username", ar: "ابحث بالاسم أو اسم المستخدم" },
  person: { en: "Person", ar: "الشخص" },
  tenant: { en: "Organisation", ar: "المؤسسة" },
  // Shown when the roster spans tenants but the registry could not be read to name one of them. "Another
  // organisation" is the true statement; a uuid would only look like one.
  otherOrg: { en: "Another organisation", ar: "مؤسسة أخرى" },
  roles: { en: "Roles", ar: "الأدوار" },
  level: { en: "Tier", ar: "الفئة" },
  status: { en: "Status", ar: "الحالة" },
  exceptions: { en: "Exceptions", ar: "الاستثناءات" },
  none: { en: "None", ar: "لا شيء" },
  expiredBadge: { en: "lapsed", ar: "منتهية" },
  platformAdmin: { en: "Platform administration", ar: "إدارة المنصّة" },
  platformAdminNote: {
    en: "Administrative authority only — it never grants access to patient data.",
    ar: "صلاحية إدارية فقط — لا تمنح الوصول إلى بيانات المرضى.",
  },
  open: { en: "Open", ar: "فتح" },
  actions: { en: "Actions", ar: "إجراءات" },
  back: { en: "Back to the list", ar: "العودة إلى القائمة" },

  detailTitle: { en: "Membership", ar: "العضوية" },
  tabRoles: { en: "Roles", ar: "الأدوار" },
  tabOverrides: { en: "Exceptions", ar: "الاستثناءات" },
  tabGrants: { en: "Branch reach", ar: "نطاق الفروع" },
  tabSessions: { en: "Sessions", ar: "الجلسات" },
  tabPreview: { en: "Effective access", ar: "الصلاحيات الفعلية" },
  tabsLabel: { en: "Membership sections", ar: "أقسام العضوية" },

  role: { en: "Role", ar: "الدور" },
  levelHelp: {
    en: "Tier answers “is this an administrative persona”. What someone can do is the effective-access tab, never the tier.",
    ar: "الفئة تجيب: هل هذه شخصية إدارية؟ أمّا ما يستطيع فعله فهو في تبويب الصلاحيات الفعلية.",
  },
  unclassified: { en: "Unclassified", ar: "غير مصنّف" },

  scope: { en: "Permission", ar: "الصلاحية" },
  effect: { en: "Effect", ar: "الأثر" },
  allow: { en: "Allow", ar: "سماح" },
  deny: { en: "Deny", ar: "منع" },
  reason: { en: "Reason", ar: "السبب" },
  grantedBy: { en: "Granted by", ar: "مُنح بواسطة" },
  expires: { en: "Expires", ar: "ينتهي" },
  neverExpires: { en: "No expiry", ar: "بلا انتهاء" },
  expired: { en: "Lapsed", ar: "منتهٍ" },
  overridesEmpty: { en: "No exceptions on this membership.", ar: "لا توجد استثناءات على هذه العضوية." },
  addOverride: { en: "Add an exception", ar: "إضافة استثناء" },
  overrideHelp: {
    en: "An exception overrides this person’s roles for one permission. Deny always wins over Allow.",
    ar: "الاستثناء يتجاوز أدوار هذا الشخص لصلاحية واحدة. المنع يتقدّم دائمًا على السماح.",
  },
  reasonRequired: { en: "A reason is required — an unexplained exception cannot be reviewed later.", ar: "السبب مطلوب — لا يمكن مراجعة استثناء بلا تبرير." },
  scopeRequired: { en: "Choose the permission this exception applies to.", ar: "اختر الصلاحية التي ينطبق عليها الاستثناء." },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  sodBlocked: { en: "Segregation of duties blocks this exception", ar: "الفصل بين المهام يمنع هذا الاستثناء" },

  branch: { en: "Branch", ar: "الفرع" },
  // A grant naming a branch the directory no longer carries. Said plainly, because it is a review finding —
  // reach into a clinic that has been decommissioned — and not a loading state.
  branchUnknown: { en: "Branch no longer listed", ar: "فرع لم يعد مُدرجًا" },
  home: { en: "Home", ar: "الرئيسي" },
  from: { en: "From", ar: "من" },
  until: { en: "Until", ar: "حتى" },
  openEnded: { en: "Open-ended", ar: "مفتوح" },
  grantsEmpty: { en: "No branch grants — this membership reaches no branch-scoped data.", ar: "لا توجد منح فروع — هذه العضوية لا تصل إلى بيانات مرتبطة بفرع." },
  grantsNote: {
    en: "Reach is not authority. These grants say which branch’s data is visible, not what may be done with it.",
    ar: "النطاق ليس صلاحية. هذه المنح تحدّد بيانات أي فرع تظهر، لا ما يمكن فعله بها.",
  },

  device: { en: "Device", ar: "الجهاز" },
  signedIn: { en: "Signed in", ar: "بدأت" },
  lastSeen: { en: "Last active", ar: "آخر نشاط" },
  revoke: { en: "Revoke", ar: "إنهاء" },
  sessionsEmpty: { en: "No active sessions.", ar: "لا توجد جلسات نشطة." },
  revokeTitle: { en: "Revoke this session?", ar: "إنهاء هذه الجلسة؟" },
  revokeBody: {
    en: "The device is signed out on its next request. Other sessions are untouched.",
    ar: "سيتم إخراج الجهاز عند طلبه التالي. الجلسات الأخرى لا تتأثر.",
  },
  previewNote: {
    en: "Recomputed on the server from this membership’s roles and exceptions — the same evaluator that issues tokens.",
    ar: "يُحتسب على الخادم من أدوار هذه العضوية واستثناءاتها — بالمقيّم نفسه الذي يُصدر الرموز.",
  },
} satisfies Record<string, Localized>;

/** Tier is an ordinal where LOWER is more privileged; 0 means no classified role. */
function levelLabel(level: number, t: (l: Localized) => string) {
  return level > 0 ? `T${level}` : t(S.unclassified);
}

/**
 * The roster — every membership in the tenant.
 *
 * Tenant-pinned by the SERVER: asking for another tenant is 403 + audited, not silently narrowed to your
 * own, because a page of your own tenant under another tenant's heading is worse than an error.
 */
export function MembershipRoster() {
  const api = useApi();
  const t = useLoc();
  const tenantNames = useTenantNames();
  const [query, setQuery] = useState("");
  const [topTab, setTopTab] = useState("accounts");
  const [selected, setSelected] = useState<string | null>(null);
  const rows = useAsync<MembershipRow[]>(() => api.memberships(undefined, undefined, query || undefined), [query]);

  if (selected) return <MembershipDetailScreen membershipId={selected} onBack={() => setSelected(null)} />;

  /*
    28.10 — THE ORGANISATION COLUMN, WHEN THERE IS MORE THAN ONE ORGANISATION.

    It rendered `r.tenantId` — a raw uuid — in every row. Two things were wrong with that. The uuid is a join
    key that the person reading a staff roster has no use for and cannot act on; and the roster is
    tenant-pinned by the server (`ResolveTenantReachAsync`), so for an org admin every row carried the SAME
    uuid. A column with one distinct value in it is not information, it is furniture.

    It survives only for the caller it means something to — a platform admin, whose reach spans tenants — and
    it renders the NAME, which is also the only form they could act on.
  */
  const list = rows.data ?? [];
  const multiTenant = new Set(list.map((r) => r.tenantId)).size > 1;

  const cols: Column<MembershipRow>[] = [
    {
      key: "person",
      header: t(S.person),
      cell: (r) => (
        <span>
          {r.displayName}
          <span className="muted"> · {r.username}</span>
        </span>
      ),
    },
    ...(multiTenant
      ? [{
          key: "tenant",
          header: t(S.tenant),
          cell: (r: MembershipRow) => tenantLabel(r.tenantId, tenantNames, t(S.otherOrg)),
          sortable: true,
          sortValue: (r: MembershipRow) => tenantLabel(r.tenantId, tenantNames, t(S.otherOrg)),
        } satisfies Column<MembershipRow>]
      : []),
    { key: "roles", header: t(S.roles), cell: (r) => (r.roles.length ? r.roles.map((x) => x.name).join(", ") : "—") },
    { key: "level", header: t(S.level), cell: (r) => <StatusChip kind="neu" label={levelLabel(r.level, t)} /> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    {
      // Exceptions get their own column because they are the reviewable surface: a role is policy, an
      // exception is somebody's decision about one person, and the lapsed count is called out separately
      // because an override quietly expiring changes authority with nobody being told.
      key: "exceptions",
      header: t(S.exceptions),
      cell: (r) =>
        r.overrideCount === 0 ? (
          <span className="muted">{t(S.none)}</span>
        ) : (
          <span>
            <span className="tnum">{r.overrideCount}</span>
            {r.expiredOverrideCount > 0 ? (
              <>
                {" "}
                <StatusChip kind="warn" label={`${r.expiredOverrideCount} ${t(S.expiredBadge)}`} />
              </>
            ) : null}
          </span>
        ),
    },
    {
      key: "open",
      // Named, not blank. An empty `<th>` is announced as a column with no name, so a screen-reader user
      // working across a row reaches a button with nothing to say what column it belongs to.
      header: t(S.actions),
      cell: (r) => (
        <Button variant="ghost" size="sm" onClick={() => setSelected(r.membershipId)}>
          {t(S.open)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.rosterTitle)} />
      {/*
        28.8 — TWO VIEWS OF THE SAME PEOPLE, and the split is the design 40 §1 invariant made visible.

        ACCOUNTS is the identity: who exists, how they sign in, which portals they hold, and the lifecycle
        (invite, reset, deprovision, restore). MEMBERSHIPS is the principal: what a person may DO inside one
        organisation — their tier, their exceptions, their reach, their effective access. The same human
        appears in both and they are not the same subject, which is exactly why authority is keyed on the
        membership and never on the identity.

        Accounts is first because it is the one that answers "make this person exist", which is the question
        this screen previously could not answer at all.
      */}
      <Tabs
        aria-label={t(S.rosterTabsLabel)}
        value={topTab}
        onValueChange={setTopTab}
        items={[
          { value: "accounts", label: t(S.tabAccounts), content: <AccountsTab /> },
          {
            value: "memberships",
            label: t(S.tabMemberships),
            content: (
              <Card as="section" style={{ padding: "var(--sp3)" }}>
                <div className="admin-toolbar">
                  <SearchField
                    aria-label={t(S.search)}
                    placeholder={t(S.search)}
                    value={query}
                    onChange={(e) => setQuery(e.currentTarget.value)}
                  />
                </div>
                <AsyncSection<MembershipRow[]> state={rows} isEmpty={(d) => d.length === 0} emptyLabel={S.rosterEmpty}>
                  {(list) => (
                    <DataTable columns={cols} rows={list} rowKey={(r) => r.membershipId} caption={t(S.rosterTitle)} />
                  )}
                </AsyncSection>
              </Card>
            ),
          },
        ]}
      />
    </>
  );
}

/**
 * The accounts half — the identity store, with the lifecycle this app has never had.
 *
 * The columns are chosen to answer, without opening a row, the four questions an administrator opens this
 * page holding: who is this, how do they sign in, what can they reach, and is the account in a state that
 * works. `twoFactorEnabled` earns its place because MFA gates every admin scope and every break-glass
 * request on the platform, and until 18.C2 no screen anywhere showed whether an account had one.
 */
function AccountsTab() {
  const api = useApi();
  const t = useLoc();
  const [query, setQuery] = useState("");
  const [reloadKey, setReloadKey] = useState(0);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<IdentityUser | null>(null);
  const [action, setAction] = useState<{ kind: "reset" | "deactivate" | "reactivate"; user: IdentityUser } | null>(null);
  // Announced rather than merely rendered: creating an account and sending a link both succeed by changing
  // almost nothing on screen, and an outcome nobody is told about reads as a button that did not work.
  const [announcement, setAnnouncement] = useState<Localized | null>(null);
  const users = useAsync<IdentityUser[]>(() => api.identityUsers(query || undefined), [query, reloadKey]);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);

  const cols: Column<IdentityUser>[] = [
    {
      key: "person",
      header: t(S.person),
      cell: (u) => (
        <span>
          {u.displayName}
          <span className="muted"> · {u.username}</span>
        </span>
      ),
      sortable: true,
      sortValue: (u) => u.displayName,
    },
    {
      /*
        28.13 — the POSITION, and it sits next to the roles on purpose so the difference between them is
        visible rather than explained. A position is what the organisation calls the job; a role is what the
        platform will let the account do. They do not have to agree, and when they disagree this column is
        the only place that says so.
      */
      key: "position",
      header: t(S.position),
      cell: (u) => (u.position ? u.position : <span className="muted">{t(S.noPosition)}</span>),
      sortable: true,
      sortValue: (u) => u.position ?? "",
    },
    {
      key: "email",
      header: t(S.email),
      // An account with no address is the one this column exists to surface: it cannot sign in by address
      // and cannot be sent a reset link, and nothing else on the page would say so.
      cell: (u) => (u.email ? u.email : <StatusChip kind="warn" label={t(S.noEmail)} />),
    },
    {
      key: "portals",
      header: t(S.portalsCol),
      cell: (u) => {
        const held = PORTALS.filter((p) => u.roles.includes(issuerRoleFor(p.role)));
        return held.length ? held.map((p) => t(p.title)).join(", ") : <span className="muted">{t(S.none)}</span>;
      },
    },
    {
      key: "twoFactor",
      header: t(S.twoFactor),
      cell: (u) =>
        u.twoFactorEnabled ? (
          <StatusChip kind="ok" label={t(S.mfaOn)} />
        ) : (
          <StatusChip kind="warn" label={t(S.mfaOff)} />
        ),
    },
    { key: "status", header: t(S.status), cell: (u) => <AccountStatusChip user={u} /> },
    {
      key: "actions",
      header: t(S.actions),
      cell: (u) => (
        <UserRowActions
          user={u}
          onAct={(kind, user) => { setAnnouncement(null); setAction({ kind, user }); }}
          onEdit={(user) => { setAnnouncement(null); setEditing(user); }}
        />
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      {/*
        28.10 — ONE toolbar row, not three stacked ones.

        This was a `.pagehead-actions` div (a PAGE-header utility, used inside a card, so the button floated
        alone on its own line), then the announcement slot, then the search field on a third line. Three bands
        of chrome above the table, and the search — the control anyone actually reaches for — was the one
        pushed furthest down. Search and the primary action belong on the same line, which is the pattern
        `DataTableView`'s own toolbar uses everywhere else in the app.
      */}
      <div className="admin-toolbar">
        <SearchField
          aria-label={t(S.searchAccounts)}
          placeholder={t(S.searchAccounts)}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
        />
        <Button
          variant="primary"
          leadingIcon={<Icon name="plus" />}
          onClick={() => { setAnnouncement(null); setCreating(true); }}
        >
          {t(USER_ADMIN_STRINGS.addUser)}
        </Button>
      </div>

      {/* Cleared whenever another action starts — see the row handlers. A success banner left standing above
          a table the administrator has since re-searched describes something that is no longer on screen. */}
      <div aria-live="polite">
        {announcement && <InlineAlert tone="ok">{t(announcement)}</InlineAlert>}
      </div>

      <AsyncSection<IdentityUser[]> state={users} isEmpty={(d) => d.length === 0} emptyLabel={S.accountsEmpty}>
        {(list) => <DataTable columns={cols} rows={list} rowKey={(u) => u.id} caption={t(S.tabAccounts)} />}
      </AsyncSection>

      <CreateUserDialog
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={({ resetLinkSent }) => {
          // The un-invited case is NOT smoothed over. The account exists and nobody can sign in to it; an
          // administrator told only "created" would walk away from that.
          setAnnouncement(resetLinkSent ? USER_ADMIN_STRINGS.createdInvited : USER_ADMIN_STRINGS.createdNotInvited);
          reload();
        }}
      />
      <EditUserDialog
        open={editing !== null}
        user={editing}
        onClose={() => setEditing(null)}
        onSaved={(message) => {
          setEditing(null);
          setAnnouncement(message);
          reload();
        }}
      />
      <UserActionDialog
        kind={action?.kind ?? null}
        user={action?.user ?? null}
        onClose={() => setAction(null)}
        onDone={(message) => {
          setAnnouncement(message);
          reload();
        }}
      />
    </Card>
  );
}

/** One membership, in five tabs: authority, exceptions, reach, sessions, and what it all adds up to. */
export function MembershipDetailScreen({ membershipId, onBack }: { membershipId: string; onBack?: () => void }) {
  const api = useApi();
  const t = useLoc();
  const tenantNames = useTenantNames();
  const [tab, setTab] = useState("roles");
  const [reloadKey, setReloadKey] = useState(0);
  const detail = useAsync<MembershipDetail>(() => api.membership(membershipId), [membershipId, reloadKey]);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);
  // Empty fallback, deliberately: a uuid resolves to "" and the segment is omitted. Naming the reader's own
  // organisation "Another organisation" on their own membership page would be worse than saying nothing.
  const tenantName = detail.data ? tenantLabel(detail.data.tenantId, tenantNames, "") : "";

  return (
    <>
      <PageHeader
        title={t(S.detailTitle)}
        actions={
          onBack ? (
            <Button variant="ghost" onClick={onBack}>
              {t(S.back)}
            </Button>
          ) : undefined
        }
      />
      <AsyncSection<MembershipDetail> state={detail} isEmpty={() => false} emptyLabel={S.rosterEmpty}>
        {(m) => (
          <>
            <Card as="section" style={{ padding: "var(--sp3)", marginBottom: "var(--sp3)" }}>
              <h2 className="panel-h">
                {m.displayName} <span className="muted">· {m.username}</span>
              </h2>
              {/* The tenant uuid used to lead this line. It named the organisation the reader is already in,
                  in a form they cannot use — so it is a name when one can be resolved, and absent otherwise
                  rather than replaced by a placeholder that says nothing. */}
              <p className="muted" style={{ margin: 0 }}>
                {tenantName ? <>{tenantName} · </> : null}
                <StatusChip kind={m.status.kind} label={t(m.status.label)} /> · {levelLabel(m.level, t)}
              </p>
              {m.isPlatformAdmin ? (
                // Stated wherever the flag appears. A1 is the invariant most easily misread as "can see
                // everything", and an administrator who believes that will use it as a debugging tool.
                <InlineAlert tone="info">
                  <strong>{t(S.platformAdmin)}</strong> — {t(S.platformAdminNote)}
                </InlineAlert>
              ) : null}
            </Card>

            <Tabs
              aria-label={t(S.tabsLabel)}
              value={tab}
              onValueChange={setTab}
              items={[
                { value: "roles", label: t(S.tabRoles), content: <RolesTab membership={m} /> },
                {
                  value: "overrides",
                  label: t(S.tabOverrides),
                  content: <OverridesTab membership={m} onChanged={reload} />,
                },
                { value: "grants", label: t(S.tabGrants), content: <GrantsTab membership={m} /> },
                { value: "sessions", label: t(S.tabSessions), content: <SessionsTab membership={m} /> },
                { value: "preview", label: t(S.tabPreview), content: <PreviewTab membership={m} /> },
              ]}
            />
          </>
        )}
      </AsyncSection>
    </>
  );
}

function RolesTab({ membership }: { membership: MembershipDetail }) {
  const t = useLoc();
  const cols: Column<{ name: string; level: number | null }>[] = [
    { key: "role", header: t(S.role), cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
    {
      key: "level",
      header: t(S.level),
      cell: (r) => <StatusChip kind="neu" label={r.level === null ? t(S.unclassified) : `T${r.level}`} />,
    },
  ];
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.levelHelp)}</p>
      {membership.roles.length === 0 ? (
        <InlineAlert tone="info">{t(S.none)}</InlineAlert>
      ) : (
        <DataTable columns={cols} rows={membership.roles} rowKey={(r) => r.name} caption={t(S.tabRoles)} />
      )}
    </Card>
  );
}

/**
 * Exceptions — the SoD-guarded exception path (§2).
 *
 * The reason field is required in the form as well as on the server, and the SoD conflict is surfaced
 * INLINE as a blocking error rather than as a toast: a 409 that disappears after four seconds leaves the
 * administrator with a form they believe they submitted.
 */
function OverridesTab({ membership, onChanged }: { membership: MembershipDetail; onChanged: () => void }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const write = useWrite();
  const actorName = useActorNames();
  // 28.9 — the same catalogue the Access Catalogue screen reads, so the two can never offer different keys.
  const catalogue = useAsync<ScopeCatalogEntry[]>(() => api.scopeCatalog(), []);
  const [open, setOpen] = useState(false);
  const [scope, setScope] = useState("");
  const [effect, setEffect] = useState<"Allow" | "Deny">("Allow");
  const [reason, setReason] = useState("");
  const [validUntil, setValidUntil] = useState("");
  const [touched, setTouched] = useState(false);

  const scopeError = touched && !scope.trim() ? t(S.scopeRequired) : undefined;
  const reasonError = touched && !reason.trim() ? t(S.reasonRequired) : undefined;

  const submit = async () => {
    setTouched(true);
    if (!scope.trim() || !reason.trim()) return;
    const ok = await write.run(() =>
      api.setMembershipOverride(membership.membershipId, {
        scopeKey: scope.trim(),
        effect,
        reason: reason.trim(),
        validUntil: validUntil ? new Date(validUntil).toISOString() : null,
      }),
    );
    if (ok) {
      setOpen(false);
      setScope("");
      setReason("");
      setValidUntil("");
      setTouched(false);
      onChanged();
    }
  };

  const cols: Column<MembershipOverride>[] = [
    { key: "scope", header: t(S.scope), cell: (r) => <span className="mono">{r.scope}</span>, sortable: true, sortValue: (r) => r.scope },
    {
      key: "effect",
      header: t(S.effect),
      // Deny is `bad` and Allow is `ok`: the two are opposite decisions and must never be told apart by
      // reading the word alone (four-cue status, 21-accessibility).
      cell: (r) => <StatusChip kind={r.effect === "Deny" ? "bad" : "ok"} label={t(r.effect === "Deny" ? S.deny : S.allow)} />,
    },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason, sortable: true, sortValue: (r) => r.reason },
    // The PERSON who authorised it, not their subject claim. See `useActorNames`.
    { key: "grantedBy", header: t(S.grantedBy), cell: (r) => <span className="muted">{actorName(r.grantedBy)}</span> },
    {
      key: "expires",
      header: t(S.expires),
      cell: (r) =>
        r.expired ? (
          <StatusChip kind="warn" label={t(S.expired)} />
        ) : r.validUntil ? (
          <span className="tnum">{fmt.date(r.validUntil)}</span>
        ) : (
          <span className="muted">{t(S.neverExpires)}</span>
        ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.overrideHelp)}</p>

      {membership.overrides.length === 0 ? (
        <InlineAlert tone="info">{t(S.overridesEmpty)}</InlineAlert>
      ) : (
        <DataTable columns={cols} rows={membership.overrides} rowKey={(r) => r.id} caption={t(S.tabOverrides)} />
      )}

      <Button variant="secondary" onClick={() => setOpen(true)} style={{ marginTop: "var(--sp3)" }}>
        {t(S.addOverride)}
      </Button>

      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t(S.addOverride)}
        description={t(S.overrideHelp)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
            <Button leadingIcon={<Icon name="check2" />} variant="primary" onClick={submit} disabled={write.busy}>{t(S.save)}</Button>
          </>
        }
      >
        {/*
          The server's refusal renders HERE, in the form, next to the thing that has to change — not as a
          toast. An SoD conflict arrives as a 409 whose detail names both halves of the split duty, and a
          message that disappears after four seconds leaves the administrator holding a form they believe
          they submitted. InlineAlert's `bad` tone carries role="alert" itself, so it is announced.
        */}
        {write.error ? (
          <InlineAlert tone="bad">
            {/* Only a 409 is the SoD refusal. Labelling a 422 or a network blip as one would send the
                administrator looking for a duty conflict that does not exist. */}
            {write.error.status === 409 ? <strong>{t(S.sodBlocked)}: </strong> : null}
            {t(write.error.message)}
          </InlineAlert>
        ) : null}

        {/*
          28.9 — CHOSEN FROM THE CATALOGUE, not typed from memory.
          ------------------------------------------------------------------------------------------------
          This was a bare text field. Granting an exception therefore required knowing the exact spelling of
          a key with no screen anywhere that listed them — so the realistic options were "give them a bigger
          role" or "guess", and a typo produced a 422 rather than a grant. A combobox over the same catalogue
          the Access Catalogue screen renders makes the narrow, reviewable path the easy one, which is the
          only way an exception mechanism competes with over-granting.

          Service-only keys are excluded: the server refuses them for a human principal, and offering one
          would be offering a control that fails.
        */}
        <ComboboxField
          id="override-scope"
          label={t(S.scope)}
          value={scope}
          error={scopeError}
          onChange={(v) => setScope(v)}
          options={(catalogue.data ?? [])
            .filter((s) => !s.serviceOnly)
            .map((s) => ({
              value: s.name,
              label: s.name,
              // Searchable by what it DOES as well as by its key: an administrator looking for "let them
              // upload a result" does not know it is spelled `lab:result:write`.
              keywords: `${s.domain} ${s.description ?? ""}`,
              hint: s.description ?? undefined,
            }))}
        />
        <fieldset className="mrs-choice" style={{ margin: "var(--sp3) 0" }}>
          <legend className="mrs-label">{t(S.effect)}</legend>
          {(["Allow", "Deny"] as const).map((v) => (
            <label key={v} className="mrs-choice-opt">
              <input
                type="radio"
                name="effect"
                value={v}
                checked={effect === v}
                onChange={() => setEffect(v)}
              />
              <span>{t(v === "Deny" ? S.deny : S.allow)}</span>
            </label>
          ))}
        </fieldset>
        <TextareaField
          label={t(S.reason)}
          value={reason}
          error={reasonError}
          onChange={(e) => setReason(e.currentTarget.value)}
        />
        <InputField
          type="date"
          label={t(S.expires)}
          help={t(S.neverExpires)}
          value={validUntil}
          onChange={(e) => setValidUntil(e.currentTarget.value)}
        />
      </Modal>
    </Card>
  );
}

/** Reach (§3) — which branches this membership can see, and until when. */
function GrantsTab({ membership }: { membership: MembershipDetail }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const actorName = useActorNames();
  const grants = useAsync<BranchScopeGrant[]>(
    () => api.branchScopeGrants(membership.userId, membership.tenantId),
    [membership.userId, membership.tenantId],
  );
  /*
    28.10 — THE BRANCH, BY NAME.

    This column rendered `r.branchId.slice(0, 8)`: the first eight characters of a uuid. That is worse than
    printing the whole thing. It cannot be copied into anything that would accept it, eight hex characters
    are not guaranteed distinct between two clinics, and it reads as a rendering bug rather than as a value.

    The question this tab answers is "which clinics can this person see" — and "Maadi" is the answer to it.
    `/branches` is org reference data readable by any authenticated caller, so the lookup costs nothing that
    the caller was not already entitled to. When a grant names a branch the directory does not carry (a
    decommissioned clinic still inside an open-ended grant, which is exactly the row a reviewer should catch),
    the code is shown as itself rather than silently blanked.
  */
  const branches = useAsync<BranchSummary[]>(() => api.branches(), []);
  const branchName = useMemo(() => {
    const map = new Map<string, string>();
    for (const b of branches.data ?? []) map.set(b.id, t(b.name));
    return map;
  }, [branches.data, t]);

  const cols: Column<BranchScopeGrant>[] = [
    {
      key: "branch",
      header: t(S.branch),
      cell: (r) => (
        <span>
          {branchName.get(r.branchId) ?? <span className="muted">{t(S.branchUnknown)}</span>}
          {r.isHome ? <> <StatusChip kind="info" label={t(S.home)} /></> : null}
        </span>
      ),
      sortable: true,
      sortValue: (r) => branchName.get(r.branchId) ?? "",
    },
    { key: "from", header: t(S.from), cell: (r) => <span className="tnum">{fmt.date(r.validFrom)}</span>, sortable: true, sortValue: (r) => r.validFrom },
    {
      key: "until",
      header: t(S.until),
      cell: (r) =>
        r.validUntil ? <span className="tnum">{fmt.date(r.validUntil)}</span> : <span className="muted">{t(S.openEnded)}</span>,
    },
    { key: "grantedBy", header: t(S.grantedBy), cell: (r) => <span className="muted">{actorName(r.grantedBy)}</span> },
    // The reason is a column, not a tooltip: "covering Alexandria for October" is what makes an expiring
    // grant reviewable, and a reviewer working down a list will not hover every row.
    { key: "reason", header: t(S.reason), cell: (r) => r.grantedReason ?? "—", sortable: true, sortValue: (r) => r.grantedReason },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.grantsNote)}</p>
      <AsyncSection<BranchScopeGrant[]> state={grants} isEmpty={(d) => d.length === 0} emptyLabel={S.grantsEmpty}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.grantId} caption={t(S.tabGrants)} />}
      </AsyncSection>
    </Card>
  );
}

function SessionsTab({ membership }: { membership: MembershipDetail }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const write = useWrite();
  const [target, setTarget] = useState<AccessSession | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const sessions = useAsync<AccessSession[]>(() => api.accessSessions(membership.userId), [membership.userId, reloadKey]);

  const confirm = async () => {
    if (!target) return;
    const ok = await write.run(() => api.revokeAccessSession(membership.userId, target.sessionId));
    if (ok) {
      setTarget(null);
      setReloadKey((k) => k + 1);
    }
  };

  const cols: Column<AccessSession>[] = [
    { key: "device", header: t(S.device), cell: (r) => r.device, sortable: true, sortValue: (r) => r.device },
    { key: "signedIn", header: t(S.signedIn), cell: (r) => <span className="tnum">{fmt.dateTime(r.createdAt)}</span>, sortable: true, sortValue: (r) => r.createdAt },
    {
      key: "lastSeen",
      header: t(S.lastSeen),
      cell: (r) => <span className="tnum">{r.lastSeenAt ? fmt.dateTime(r.lastSeenAt) : "—"}</span>,
    },
    {
      key: "revoke",
      header: t(S.actions),
      cell: (r) => (
        <Button variant="danger" size="sm" onClick={() => setTarget(r)}>
          {t(S.revoke)}
        </Button>
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}
      <AsyncSection<AccessSession[]> state={sessions} isEmpty={(d) => d.length === 0} emptyLabel={S.sessionsEmpty}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.sessionId} caption={t(S.tabSessions)} />}
      </AsyncSection>

      <Modal
        open={target !== null}
        onOpenChange={(o) => !o && setTarget(null)}
        title={t(S.revokeTitle)}
        description={t(S.revokeBody)}
        footer={
          <>
            <Button variant="secondary" onClick={() => setTarget(null)}>{t(S.cancel)}</Button>
            <Button variant="danger" onClick={confirm} disabled={write.busy}>{t(S.revoke)}</Button>
          </>
        }
      >
        <p>{target?.device}</p>
      </Modal>
    </Card>
  );
}

/**
 * The effective-access preview — mode 2, rendered verbatim.
 *
 * This tab exists because the four above answer "what has been configured" and none of them answers "what
 * can this person actually do". The set algebra is deny-wins across roles and exceptions, and nobody can
 * run it in their head from four tables.
 */
function PreviewTab({ membership }: { membership: MembershipDetail }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<EffectiveAccess>(() => api.effectiveAccess(membership.membershipId), [membership.membershipId]);

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.previewNote)}</p>
      <EffectiveAccessPreview
        membershipId={membership.membershipId}
        keys={state.data?.keys ?? []}
        loading={state.status === "loading"}
        error={state.status === "error" ? state.error?.message : undefined}
      />
    </Card>
  );
}
