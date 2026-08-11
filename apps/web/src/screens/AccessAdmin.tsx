import { useCallback, useMemo, useState } from "react";
import {
  Button,
  Card,
  ComboboxField,
  DataTable,
  DataTableView,
  Icon,
  InputField,
  InlineAlert,
  Modal,
  StatusChip,
  Tabs,
  TextareaField,
  useTableQuery,
} from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
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
import { StaffAvatar } from "../shell/StaffAvatar";
import { AsyncSection, PageHeader, tenantLabel, useActorNames, useLoc, useTenantNames } from "./_shared";
import { EffectiveAccessPreview } from "./EffectiveAccessPreview";
import {
  AccountDetailsForm,
  AccountLifecycleActions,
  AccountStatusChip,
  CreateUserDialog,
  PortalChecklist,
  UserActionDialog,
  USER_ADMIN_STRINGS,
  portalsHeldBy,
} from "./UserAdmin";

/**
 * Phase 28.16 — USERS & ACCESS: one table of people, one record per person.
 *
 * ============================================================================================================
 * WHAT CHANGED, AND WHY THE OLD SHAPE WAS WRONG
 * ============================================================================================================
 * This screen was two tabs over two endpoints. "Accounts" listed identities — name, address, portals, second
 * factor, lifecycle. "Authority" listed memberships — name, organisation, roles, tier, status, exceptions.
 * Both tabs were a list of the same colleagues, sorted the same way, with the same person's name repeated in
 * the first column of each, and an administrator asking the only question anybody actually opens this screen
 * with — "what does Sara have, and is it right?" — had to read one tab, remember it, and go to the other.
 *
 * Four of the fourteen columns across those two tables were literally the same column (person, status ×2),
 * and two more were the same fact in two vocabularies: the accounts tab's PORTALS were resolved from exactly
 * the issuer roles the authority tab printed raw. Nothing was gained by the split except the split itself.
 *
 * ============================================================================================================
 * WHAT THE MERGE MUST NOT DO — INVARIANT 1 IS STILL INVARIANT 1
 * ============================================================================================================
 * Design 40 §1: authorization evaluates against the MEMBERSHIP, never the identity. The same person
 * legitimately holds different authority in two organisations and those two sets are never unioned.
 *
 * So the merge is of the LIST, not of the principal. The table is a directory of people, because a directory
 * is what an administrator navigates. The moment authority is configured, the screen makes you name the
 * membership you are configuring: a person holding two shows a membership switch, and every exception,
 * branch grant and effective-access answer below it belongs to the one that is selected. A blended
 * "everything Sara can do" view is the thing the design forbids, and it is not available here.
 *
 * The one summary the row does carry across memberships is a COUNT — how many exceptions, how many lapsed —
 * because a count is a reason to look, not a statement about what somebody may do.
 *
 * ============================================================================================================
 * ONE RECORD, TWO TABS: WHO THEY ARE, AND WHAT THEY MAY DO
 * ============================================================================================================
 * Seven tab-stops became two. The old shape was 2 roster tabs, a 5-tab membership detail, and an "Edit"
 * dialog that carried the portals — so "give Sara the pharmacy portal and take back her export exception"
 * was two tabs, one dialog and two round trips through the list.
 *
 * The split that survives is the only one that answers a different question:
 *   ACCESS  — portals, exceptions from the catalogue, branch reach, and what it all adds up to.
 *   ACCOUNT — name, address, position, photo, second factor, sessions, and the lifecycle.
 *
 * Roles as their own tab is gone: a tab that rendered a two-column read-only table of what the portals
 * checklist immediately above it now sets was a description of the control beside it.
 *
 * UI GATING HERE IS COSMETIC (§6). Hiding an action a caller cannot use is a usability courtesy; every action
 * re-checks on the server, and `UiGatingIsCosmeticTests` hand-crafts the request behind each hidden
 * affordance and asserts the API refuses it.
 */

const S = {
  title: { en: "Users & Access", ar: "المستخدمون والصلاحيات" },
  lede: {
    en: "Everyone who can sign in, what they may reach, and the exceptions someone decided on.",
    ar: "كل من يمكنه تسجيل الدخول، وما يمكنه الوصول إليه، والاستثناءات التي قرّرها أحدهم.",
  },
  search: {
    en: "Search by name, username, email, position or portal",
    ar: "ابحث بالاسم أو اسم المستخدم أو البريد أو المسمى الوظيفي أو البوابة",
  },
  empty: { en: "No accounts yet. Add one to get started.", ar: "لا توجد حسابات بعد. أضف حسابًا للبدء." },
  noMatches: {
    en: "No one matches. Change the search or clear the filters.",
    ar: "لا أحد مطابق. عدّل البحث أو أزل عوامل التصفية.",
  },

  person: { en: "Person", ar: "الشخص" },
  noEmail: { en: "No address", ar: "بلا بريد" },
  position: { en: "Position", ar: "المسمى الوظيفي" },
  // Said rather than left blank. An empty cell reads as a rendering fault; "Not recorded" reads as a fact,
  // and it is the correct fact for a service account, which has no job title because it is not a person.
  noPosition: { en: "Not recorded", ar: "غير مسجّل" },
  portalsCol: { en: "Portals", ar: "البوابات" },
  tenant: { en: "Organisation", ar: "المؤسسة" },
  // Shown when the roster spans tenants but the registry could not be read to name one of them. "Another
  // organisation" is the true statement; a uuid would only look like one.
  otherOrg: { en: "Another organisation", ar: "مؤسسة أخرى" },
  level: { en: "Tier", ar: "الفئة" },
  status: { en: "Status", ar: "الحالة" },
  exceptions: { en: "Exceptions", ar: "الاستثناءات" },
  none: { en: "None", ar: "لا شيء" },
  expiredBadge: { en: "lapsed", ar: "منتهية" },
  twoFactor: { en: "Second factor", ar: "التحقق بخطوتين" },
  mfaOn: { en: "Two-factor on", ar: "التحقق بخطوتين مُفعّل" },
  mfaOff: { en: "No second factor", ar: "بلا تحقق بخطوتين" },
  noMembership: { en: "No membership", ar: "بلا عضوية" },
  noMembershipHelp: {
    en: "This account exists but holds authority in no organisation. It can sign in and reach nothing.",
    ar: "هذا الحساب موجود لكنه لا يحمل صلاحية في أي مؤسسة. يمكنه تسجيل الدخول دون الوصول إلى شيء.",
  },
  unclassified: { en: "Unclassified", ar: "غير مصنّف" },
  actions: { en: "Actions", ar: "إجراءات" },
  manage: { en: "Manage", ar: "إدارة" },
  back: { en: "Back to the list", ar: "العودة إلى القائمة" },

  // Filters — each one is a question somebody opens this table holding, and every one of them was
  // previously answerable only by reading every row of two separate tables.
  filterPortal: { en: "Portal", ar: "البوابة" },
  filterFlags: { en: "Needs attention", ar: "تحتاج انتباهًا" },
  flagNoPortal: { en: "No portal", ar: "بلا بوابة" },
  flagNoMfa: { en: "No second factor", ar: "بلا تحقق بخطوتين" },
  flagExceptions: { en: "Has exceptions", ar: "لديه استثناءات" },
  flagLapsed: { en: "Lapsed exception", ar: "استثناء منتهٍ" },
  flagInactive: { en: "De-provisioned", ar: "مُعطّل" },
  truncated: {
    en: "Showing the first accounts the directory returned. Search to narrow to the person you want.",
    ar: "تُعرض أوائل الحسابات التي أعادها الدليل. استخدم البحث للوصول إلى الشخص المطلوب.",
  },
  orphans: {
    en: "Some memberships in this organisation belong to accounts outside this list and are not shown.",
    ar: "بعض العضويات في هذه المؤسسة تخصّ حسابات خارج هذه القائمة ولا تظهر هنا.",
  },

  // ---- the person's record --------------------------------------------------------------------------------
  tabsLabel: { en: "This person's record", ar: "سجل هذا الشخص" },
  tabAccess: { en: "Access", ar: "الصلاحيات" },
  tabAccount: { en: "Account", ar: "الحساب" },

  membershipPicker: { en: "Configuring authority in", ar: "تهيئة الصلاحية في" },
  membershipPickerHelp: {
    en: "Authority is held per organisation and never combined. Everything below belongs to the one selected.",
    ar: "تُمنح الصلاحية لكل مؤسسة على حدة ولا تُدمج. كل ما يلي يخصّ المؤسسة المحددة.",
  },
  platformAdmin: { en: "Platform administration", ar: "إدارة المنصّة" },
  platformAdminNote: {
    en: "Administrative authority only — it never grants access to patient data.",
    ar: "صلاحية إدارية فقط — لا تمنح الوصول إلى بيانات المرضى.",
  },

  portalsPanel: { en: "Portals", ar: "البوابات" },
  portalsHelp: {
    en: "Each portal is a workspace with its own screens. Tick only what this person’s job needs — they see nothing outside them. Changes take effect on their next sign-in, or within five minutes on this one.",
    ar: "كل بوابة مساحة عمل بشاشاتها. حدّد ما تحتاجه وظيفة هذا الشخص فقط — فلن يرى شيئًا خارجها. تسري التغييرات عند تسجيل الدخول التالي أو خلال خمس دقائق على الجلسة الحالية.",
  },
  portalsRequired: {
    en: "Choose at least one portal — an account with none can sign in and reach nothing.",
    ar: "اختر بوابة واحدة على الأقل — الحساب بلا بوابة يسجّل الدخول ولا يصل إلى شيء.",
  },
  portalsSaved: { en: "Portals updated.", ar: "تم تحديث البوابات." },

  exceptionsPanel: { en: "Permission exceptions", ar: "استثناءات الصلاحيات" },
  overrideHelp: {
    en: "An exception overrides this person’s roles for one permission, chosen from the access catalogue. Deny always wins over Allow.",
    ar: "الاستثناء يتجاوز أدوار هذا الشخص لصلاحية واحدة تُختار من دليل الصلاحيات. المنع يتقدّم دائمًا على السماح.",
  },
  overridesEmpty: { en: "No exceptions on this membership.", ar: "لا توجد استثناءات على هذه العضوية." },
  addOverride: { en: "Add an exception", ar: "إضافة استثناء" },
  withdraw: { en: "Withdraw", ar: "سحب" },
  withdrawTitle: { en: "Withdraw this exception?", ar: "سحب هذا الاستثناء؟" },
  withdrawBody: {
    en: "The permission goes back to whatever this person’s roles say. The decision stays in the audit record — nothing is erased.",
    ar: "تعود الصلاحية إلى ما تقوله أدوار هذا الشخص. يبقى القرار في سجل التدقيق — لا يُمحى شيء.",
  },
  reasonRequired: { en: "A reason is required — an unexplained exception cannot be reviewed later.", ar: "السبب مطلوب — لا يمكن مراجعة استثناء بلا تبرير." },
  scopeRequired: { en: "Choose the permission this exception applies to.", ar: "اختر الصلاحية التي ينطبق عليها الاستثناء." },
  sodBlocked: { en: "Segregation of duties blocks this exception", ar: "الفصل بين المهام يمنع هذا الاستثناء" },
  scope: { en: "Permission", ar: "الصلاحية" },
  effect: { en: "Effect", ar: "الأثر" },
  allow: { en: "Allow", ar: "سماح" },
  deny: { en: "Deny", ar: "منع" },
  reason: { en: "Reason", ar: "السبب" },
  grantedBy: { en: "Granted by", ar: "مُنح بواسطة" },
  expires: { en: "Expires", ar: "ينتهي" },
  neverExpires: { en: "No expiry", ar: "بلا انتهاء" },
  expired: { en: "Lapsed", ar: "منتهٍ" },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },

  reachPanel: { en: "Branch reach", ar: "نطاق الفروع" },
  grantsNote: {
    en: "Reach is not authority. These grants say which branch’s data is visible, not what may be done with it.",
    ar: "النطاق ليس صلاحية. هذه المنح تحدّد بيانات أي فرع تظهر، لا ما يمكن فعله بها.",
  },
  branch: { en: "Branch", ar: "الفرع" },
  // A grant naming a branch the directory no longer carries. Said plainly, because it is a review finding —
  // reach into a clinic that has been decommissioned — and not a loading state.
  branchUnknown: { en: "Branch no longer listed", ar: "فرع لم يعد مُدرجًا" },
  home: { en: "Home", ar: "الرئيسي" },
  from: { en: "From", ar: "من" },
  until: { en: "Until", ar: "حتى" },
  openEnded: { en: "Open-ended", ar: "مفتوح" },
  grantsEmpty: { en: "No branch grants — this membership reaches no branch-scoped data.", ar: "لا توجد منح فروع — هذه العضوية لا تصل إلى بيانات مرتبطة بفرع." },

  effectivePanel: { en: "Effective access", ar: "الصلاحيات الفعلية" },
  previewNote: {
    en: "Recomputed on the server from this membership’s roles and exceptions — the same evaluator that issues tokens.",
    ar: "يُحتسب على الخادم من أدوار هذه العضوية واستثناءاتها — بالمقيّم نفسه الذي يُصدر الرموز.",
  },

  detailsPanel: { en: "Details", ar: "البيانات" },
  signInPanel: { en: "Signing in", ar: "تسجيل الدخول" },
  sessionsPanel: { en: "Active sessions", ar: "الجلسات النشطة" },
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
} satisfies Record<string, Localized>;

/** Tier is an ordinal where LOWER is more privileged; 0 means no classified role. */
function levelLabel(level: number, t: (l: Localized) => string) {
  return level > 0 ? `T${level}` : t(S.unclassified);
}

/**
 * One row of the merged table: an ACCOUNT, with the memberships it holds.
 *
 * <p>The account is the row key because a person is who an administrator is looking for. The memberships are
 * a list and stay a list — see the invariant note at the top of the file.</p>
 */
interface PersonRow {
  user: IdentityUser;
  memberships: MembershipRow[];
}

export function UsersAndAccess() {
  const api = useApi();
  const t = useLoc();
  const tenantNames = useTenantNames();
  const [reloadKey, setReloadKey] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  // Announced rather than merely rendered: creating an account and sending a link both succeed by changing
  // almost nothing on screen, and an outcome nobody is told about reads as a button that did not work.
  const [announcement, setAnnouncement] = useState<Localized | null>(null);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);

  const users = useAsync<IdentityUser[]>(() => api.identityUsers(), [reloadKey]);
  const memberships = useAsync<MembershipRow[]>(() => api.memberships(), [reloadKey]);

  /*
    Joined in the browser, on purpose. The two lists come from two endpoints on identity-service that answer
    two different questions and are pinned differently (memberships are tenant-pinned by the server; the
    account directory is not), and a composite endpoint would have to pick one pinning for both. Joining here
    keeps each answer exactly what its own service said, and the join key is the user id neither of them
    invents.
  */
  const rows = useMemo<PersonRow[]>(() => {
    const byUser = new Map<string, MembershipRow[]>();
    for (const m of memberships.data ?? []) {
      const list = byUser.get(m.userId) ?? [];
      list.push(m);
      byUser.set(m.userId, list);
    }
    return (users.data ?? []).map((user) => ({ user, memberships: byUser.get(user.id) ?? [] }));
  }, [users.data, memberships.data]);

  // Said out loud rather than silently dropped. A membership whose account is not in the directory listing is
  // authority this screen is not showing, and an administrator reviewing access has to know the list is
  // short — that is precisely the row an access review exists to catch.
  const orphanCount = useMemo(() => {
    const known = new Set((users.data ?? []).map((u) => u.id));
    return new Set((memberships.data ?? []).filter((m) => !known.has(m.userId)).map((m) => m.userId)).size;
  }, [users.data, memberships.data]);

  /*
    28.10's reasoning, carried over: the ORGANISATION column earns its place only for a caller whose reach
    spans tenants. The roster is tenant-pinned by the server, so for an org admin every row would carry the
    same organisation — and a column with one distinct value in it is not information, it is furniture.
  */
  const multiTenant = new Set((memberships.data ?? []).map((m) => m.tenantId)).size > 1;

  const orgOf = useCallback(
    (r: PersonRow) => [...new Set(r.memberships.map((m) => tenantLabel(m.tenantId, tenantNames, t(S.otherOrg))))],
    [tenantNames, t],
  );

  const cols: Column<PersonRow>[] = [
    {
      key: "person",
      header: t(S.person),
      /*
        The account's THREE identifying facts in one cell — face, name, and the address they sign in with —
        because they are read as one thing and were three columns across two tables. The address is the
        secondary line rather than a column of its own since it is also the default username; an account with
        no address falls back to the username and says so, which is the state this used to need a column to
        surface (it can neither sign in by address nor be sent a reset link).
      */
      cell: (r) => (
        <span className="person-cell">
          <StaffAvatar userId={r.user.id} name={r.user.displayName} size={32} />
          <span className="person-cell-text">
            <span className="person-cell-name">{r.user.displayName}</span>
            <span className="person-cell-sub muted">
              {r.user.email ? r.user.email : <>{r.user.username} <StatusChip kind="warn" label={t(S.noEmail)} /></>}
            </span>
          </span>
        </span>
      ),
      sortable: true,
      sortValue: (r) => r.user.displayName,
    },
    {
      /*
        28.13 — the POSITION, beside the portals on purpose so the difference between them is visible rather
        than explained. A position is what the organisation calls the job; a portal is what the platform will
        let the account open. They do not have to agree, and when they disagree this column is the only place
        that says so.
      */
      key: "position",
      header: t(S.position),
      cell: (r) => (r.user.position ? r.user.position : <span className="muted">{t(S.noPosition)}</span>),
      sortable: true,
      sortValue: (r) => r.user.position ?? "",
    },
    {
      key: "portals",
      header: t(S.portalsCol),
      cell: (r) => {
        const held = portalsHeldBy(r.user);
        return held.length ? held.map((p) => t(p.title)).join(", ") : <span className="muted">{t(S.none)}</span>;
      },
    },
    ...(multiTenant
      ? [{
          key: "tenant",
          header: t(S.tenant),
          cell: (r: PersonRow) => orgOf(r).join(", ") || <span className="muted">—</span>,
          sortable: true,
          sortValue: (r: PersonRow) => orgOf(r).join(", "),
        } satisfies Column<PersonRow>]
      : []),
    {
      key: "level",
      header: t(S.level),
      /*
        One chip PER MEMBERSHIP, not a blended maximum. A person holding a T1 role in one organisation and a
        T3 in another is not "a T1"; showing the more privileged of the two would state authority they hold
        nowhere. When there is exactly one membership — which is every row for a single-tenant caller — this
        renders as the single chip it always was.
      */
      cell: (r) =>
        r.memberships.length === 0 ? (
          <span className="muted">—</span>
        ) : (
          <span className="chip-row">
            {r.memberships.map((m) => (
              <StatusChip key={m.membershipId} kind="neu" label={levelLabel(m.level, t)} />
            ))}
          </span>
        ),
    },
    {
      // Exceptions get their own column because they are the reviewable surface: a role is policy, an
      // exception is somebody's decision about one person, and the lapsed count is called out separately
      // because an override quietly expiring changes authority with nobody being told.
      key: "exceptions",
      header: t(S.exceptions),
      cell: (r) => {
        const total = r.memberships.reduce((n, m) => n + m.overrideCount, 0);
        const lapsed = r.memberships.reduce((n, m) => n + m.expiredOverrideCount, 0);
        return total === 0 ? (
          <span className="muted">{t(S.none)}</span>
        ) : (
          <span className="chip-row">
            <span className="tnum">{total}</span>
            {lapsed > 0 ? <StatusChip kind="warn" label={`${lapsed} ${t(S.expiredBadge)}`} /> : null}
          </span>
        );
      },
      sortable: true,
      sortValue: (r) => r.memberships.reduce((n, m) => n + m.overrideCount, 0),
    },
    {
      /*
        The two things that stop somebody working, in one cell, because they are read together and were a
        column each in two different tables. The account's own state leads; the membership's is added only
        when it is NOT the ordinary active case, so a table of working colleagues does not carry a chip on
        every row that says nothing. Second factor is a `warn` chip only when it is missing — MFA gates every
        admin scope and every break-glass request, so its ABSENCE is the fact worth the ink.
      */
      key: "status",
      header: t(S.status),
      cell: (r) => (
        <span className="chip-row">
          <AccountStatusChip user={r.user} />
          {r.memberships
            .filter((m) => m.status.kind !== "ok")
            .map((m) => (
              <StatusChip key={m.membershipId} kind={m.status.kind} label={t(m.status.label)} />
            ))}
          {r.memberships.length === 0 ? <StatusChip kind="warn" label={t(S.noMembership)} /> : null}
          {r.user.twoFactorEnabled ? null : <StatusChip kind="warn" label={t(S.mfaOff)} />}
        </span>
      ),
      sortable: true,
      sortValue: (r) => (r.user.isActive ? "1" : "0"),
    },
    {
      key: "actions",
      // Named, not blank. An empty `<th>` is announced as a column with no name, so a screen-reader user
      // working across a row reaches a button with nothing to say what column it belongs to.
      header: t(S.actions),
      cell: (r) => (
        <Button variant="ghost" size="sm" leadingIcon={<Icon name="pen" />} onClick={() => setSelected(r.user.id)}>
          {t(S.manage)}
        </Button>
      ),
    },
  ];

  const filters: TableFilterSpec<PersonRow>[] = useMemo(() => [
    {
      key: "portal",
      label: t(S.filterPortal),
      options: [...new Map(
        rows.flatMap((r) => portalsHeldBy(r.user).map((p) => [p.role, t(p.title)] as const)),
      )].sort((a, b) => a[1].localeCompare(b[1])).map(([value, label]) => ({ value, label })),
      match: (r, value) => portalsHeldBy(r.user).some((p) => p.role === value),
    },
    {
      // Every option here is a governance finding somebody would otherwise have to read the whole table to
      // find: an account that can sign in and reach nothing, one that gates admin scopes with no second
      // factor, and an exception that lapsed without anybody being told.
      key: "flags",
      label: t(S.filterFlags),
      options: [
        { value: "no-portal", label: t(S.flagNoPortal) },
        { value: "no-mfa", label: t(S.flagNoMfa) },
        { value: "exceptions", label: t(S.flagExceptions) },
        { value: "lapsed", label: t(S.flagLapsed) },
        { value: "inactive", label: t(S.flagInactive) },
      ],
      match: (r, value) =>
        value === "no-portal" ? portalsHeldBy(r.user).length === 0
        : value === "no-mfa" ? !r.user.twoFactorEnabled
        : value === "exceptions" ? r.memberships.some((m) => m.overrideCount > 0)
        : value === "lapsed" ? r.memberships.some((m) => m.expiredOverrideCount > 0)
        : !r.user.isActive,
    },
  ], [t, rows]);

  const query = useTableQuery<PersonRow>({
    rows,
    columns: cols,
    searchText: (r) =>
      [
        r.user.displayName,
        r.user.username,
        r.user.email ?? "",
        r.user.position ?? "",
        ...portalsHeldBy(r.user).map((p) => t(p.title)),
        ...orgOf(r),
      ].join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.search),
    filters,
    pageSize: 25,
    initialSortKey: "person",
    persistKey: "users-access",
  });

  const person = selected ? rows.find((r) => r.user.id === selected) : undefined;
  if (selected) {
    // A row can vanish under the selection (the account was searched away, or de-provisioned in another tab).
    // Falling back to the list is the honest answer; rendering a record built from a stale copy is not.
    if (!person) {
      if (users.status === "success") setSelected(null);
      return <PageHeader title={t(S.title)} />;
    }
    // Keyed by the person, so opening a DIFFERENT record starts fresh rather than inheriting the last one's
    // selected membership and half-typed form.
    return <PersonRecord key={person.user.id} person={person} onBack={() => setSelected(null)} onChanged={reload} />;
  }

  return (
    <>
      <PageHeader title={t(S.title)} />
      <p className="lede">{t(S.lede)}</p>

      {/* Cleared whenever another action starts. A success banner left standing above a table the
          administrator has since re-searched describes something that is no longer on screen. */}
      <div aria-live="polite">
        {announcement && <InlineAlert tone="ok">{t(announcement)}</InlineAlert>}
      </div>
      {orphanCount > 0 && <InlineAlert tone="warn">{t(S.orphans)}</InlineAlert>}
      {(users.data?.length ?? 0) >= 200 && <InlineAlert tone="info">{t(S.truncated)}</InlineAlert>}

      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection<IdentityUser[]> state={users} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.user.id}
              caption={t(S.title)}
              emptyLabel={t(S.empty)}
              noMatchesLabel={t(S.noMatches)}
              toolbarExtra={
                <Button
                  variant="primary"
                  leadingIcon={<Icon name="plus" />}
                  onClick={() => { setAnnouncement(null); setCreating(true); }}
                >
                  {t(USER_ADMIN_STRINGS.addUser)}
                </Button>
              }
            />
          )}
        </AsyncSection>
      </Card>

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
    </>
  );
}

/**
 * One person's record — Access and Account, and nothing else.
 *
 * <p>The header states the facts both tabs are about, so neither has to repeat them: who this is, whether the
 * account works, and what tier of authority they hold where.</p>
 */
function PersonRecord({
  person,
  onBack,
  onChanged,
}: {
  person: PersonRow;
  onBack: () => void;
  onChanged: () => void;
}) {
  const t = useLoc();
  const tenantNames = useTenantNames();
  const [tab, setTab] = useState("access");
  const [announcement, setAnnouncement] = useState<Localized | null>(null);
  // Which membership is being configured. Only ever ONE — see the invariant note at the top of the file.
  const [membershipId, setMembershipId] = useState(person.memberships[0]?.membershipId ?? "");
  const membership = person.memberships.find((m) => m.membershipId === membershipId) ?? person.memberships[0];

  const announce = useCallback((message: Localized) => {
    setAnnouncement(message);
    onChanged();
  }, [onChanged]);

  return (
    <>
      <PageHeader
        title={person.user.displayName}
        actions={
          <Button variant="ghost" onClick={onBack}>
            {t(S.back)}
          </Button>
        }
      />

      <Card as="section" style={{ padding: "var(--sp3)", marginBottom: "var(--sp3)" }}>
        <div className="person-head">
          <StaffAvatar userId={person.user.id} name={person.user.displayName} size={56} />
          <div>
            <h2 className="panel-h">
              {person.user.position ? person.user.position : <span className="muted">{t(S.noPosition)}</span>}
            </h2>
            <p className="muted" style={{ margin: 0 }}>
              {person.user.email ?? person.user.username}
            </p>
            <div className="chip-row" style={{ marginTop: "var(--sp2)" }}>
              <AccountStatusChip user={person.user} />
              <StatusChip
                kind={person.user.twoFactorEnabled ? "ok" : "warn"}
                label={t(person.user.twoFactorEnabled ? S.mfaOn : S.mfaOff)}
              />
              {person.memberships.map((m) => (
                <StatusChip
                  key={m.membershipId}
                  kind={m.status.kind}
                  label={`${tenantLabel(m.tenantId, tenantNames, t(S.otherOrg))} · ${levelLabel(m.level, t)}`}
                />
              ))}
            </div>
          </div>
        </div>
        {person.memberships.some((m) => m.isPlatformAdmin) ? (
          // Stated wherever the flag appears. A1 is the invariant most easily misread as "can see
          // everything", and an administrator who believes that will use it as a debugging tool.
          <InlineAlert tone="info">
            <strong>{t(S.platformAdmin)}</strong> — {t(S.platformAdminNote)}
          </InlineAlert>
        ) : null}
      </Card>

      <div aria-live="polite">
        {announcement && <InlineAlert tone="ok">{t(announcement)}</InlineAlert>}
      </div>

      <Tabs
        aria-label={t(S.tabsLabel)}
        value={tab}
        onValueChange={setTab}
        items={[
          {
            value: "access",
            label: t(S.tabAccess),
            content: (
              <AccessTab
                user={person.user}
                memberships={person.memberships}
                membership={membership}
                onPickMembership={setMembershipId}
                onSaved={announce}
              />
            ),
          },
          {
            value: "account",
            label: t(S.tabAccount),
            content: <AccountTab user={person.user} onSaved={announce} />,
          },
        ]}
      />
    </>
  );
}

/**
 * WHAT THIS PERSON MAY DO — the whole configuration surface, in the order the decisions are made.
 *
 * <p>Portals first, because that is the grant that opens a workspace and it is the one an administrator comes
 * here to change. Exceptions second, because they are the narrow adjustment to it, chosen from the access
 * catalogue rather than typed from memory. Reach third — which branches' data those permissions see, which is
 * a different axis and says so. The server's effective-access answer last, because it is the sum of the three
 * above and reading it first would be reading an answer before the question.</p>
 */
function AccessTab({
  user,
  memberships,
  membership,
  onPickMembership,
  onSaved,
}: {
  user: IdentityUser;
  memberships: MembershipRow[];
  membership: MembershipRow | undefined;
  onPickMembership: (id: string) => void;
  onSaved: (message: Localized) => void;
}) {
  const t = useLoc();
  const tenantNames = useTenantNames();
  /*
    ONE key for the whole tab, and it is not tidiness.

    The panels here are not independent views of independent things: granting an exception changes what the
    effective-access panel below it must say, and so does ticking a portal. Each panel owning its own reload
    left the answer panel describing the state before the change — the one place on this screen where being
    out of date is indistinguishable from being wrong, because its whole job is to be the summary.
  */
  const [accessKey, setAccessKey] = useState(0);
  const changed = useCallback((message: Localized) => {
    setAccessKey((k) => k + 1);
    onSaved(message);
  }, [onSaved]);

  return (
    <div className="stack-3">
      {/*
        The membership switch, and it appears ONLY when there is a choice to make. One radio group with one
        option is a control that cannot be operated, and it would sit at the top of every record on a
        single-tenant platform announcing a distinction that does not apply there.
      */}
      {memberships.length > 1 && (
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <fieldset className="mrs-choice">
            <legend className="mrs-label">{t(S.membershipPicker)}</legend>
            <p className="muted" style={{ marginTop: 0 }}>{t(S.membershipPickerHelp)}</p>
            {memberships.map((m) => (
              <label key={m.membershipId} className="mrs-choice-opt">
                <input
                  type="radio"
                  name="membership"
                  value={m.membershipId}
                  checked={membership?.membershipId === m.membershipId}
                  onChange={() => onPickMembership(m.membershipId)}
                />
                <span>
                  {tenantLabel(m.tenantId, tenantNames, t(S.otherOrg))}
                  <span className="muted"> · {m.roles.map((r) => r.name).join(", ") || t(S.none)}</span>
                </span>
              </label>
            ))}
          </fieldset>
        </Card>
      )}

      <PortalsPanel user={user} onSaved={changed} />

      {membership ? (
        <>
          <ExceptionsPanel membershipId={membership.membershipId} reloadKey={accessKey} onChanged={changed} />
          <ReachPanel userId={membership.userId} tenantId={membership.tenantId} />
          <EffectivePanel membershipId={membership.membershipId} reloadKey={accessKey} />
        </>
      ) : (
        // An account with no membership holds authority nowhere, so there is nothing here to configure and
        // nothing to preview. Said as a fact with its consequence, rather than three empty tables.
        <InlineAlert tone="warn">{t(S.noMembershipHelp)}</InlineAlert>
      )}
    </div>
  );
}

/** The portals this account holds — the grant that opens a workspace. */
function PortalsPanel({ user, onSaved }: { user: IdentityUser; onSaved: (m: Localized) => void }) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const held = useMemo(() => portalsHeldBy(user).map((p) => p.role), [user]);
  const [chosen, setChosen] = useState<string[]>(held);
  const [touched, setTouched] = useState(false);
  const [seeded, setSeeded] = useState(user.id);

  if (seeded !== user.id) {
    setSeeded(user.id);
    setChosen(held);
    setTouched(false);
    write.reset();
  }

  const dirty = [...chosen].sort().join() !== [...held].sort().join();

  async function submit() {
    setTouched(true);
    if (chosen.length === 0 || !dirty) return;
    /*
      The write REPLACES the account's whole role set, which is what the endpoint does, and that has one
      visible consequence worth stating: an account granted through an issuer ALIAS (`network_team`,
      `imaging_tech`) is rewritten under the canonical name for the same portal. That is the correct
      direction — the alias exists for the rename's dual-accept window and the server resolves both — but it
      is a change the administrator did not ask for, so it happens only when they were saving portals anyway.
    */
    const ok = await write.run(() => api.setIdentityUserRoles(user.id, chosen.map((r) => issuerRoleFor(r as never))));
    if (ok) onSaved(S.portalsSaved);
  }

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <h3 className="panel-h">{t(S.portalsPanel)}</h3>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.portalsHelp)}</p>
      {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}
      {touched && chosen.length === 0 && <InlineAlert tone="bad">{t(S.portalsRequired)}</InlineAlert>}
      <PortalChecklist
        chosen={chosen}
        onToggle={(role, on) => setChosen((prev) => (on ? [...prev, role] : prev.filter((r) => r !== role)))}
      />
      <div className="chip-row" style={{ marginTop: "var(--sp3)" }}>
        <Button
          variant="primary"
          leadingIcon={<Icon name="check2" />}
          loading={write.busy}
          disabled={!dirty}
          onClick={() => void submit()}
        >
          {t(S.save)}
        </Button>
      </div>
    </Card>
  );
}

/**
 * Exceptions — the SoD-guarded exception path (§2), granted FROM the access catalogue and withdrawable.
 *
 * <p>The reason field is required in the form as well as on the server, and the SoD conflict is surfaced
 * INLINE as a blocking error rather than as a toast: a 409 that disappears after four seconds leaves the
 * administrator with a form they believe they submitted.</p>
 */
function ExceptionsPanel({
  membershipId,
  reloadKey,
  onChanged,
}: {
  membershipId: string;
  reloadKey: number;
  onChanged: (m: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const write = useWrite();
  const actorName = useActorNames();
  const detail = useAsync<MembershipDetail>(() => api.membership(membershipId), [membershipId, reloadKey]);
  // 28.9 — the same catalogue the Access Catalogue screen reads, so the two can never offer different keys.
  const catalogue = useAsync<ScopeCatalogEntry[]>(() => api.scopeCatalog(), []);
  const [open, setOpen] = useState(false);
  const [withdrawing, setWithdrawing] = useState<MembershipOverride | null>(null);
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
      api.setMembershipOverride(membershipId, {
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
      // The tab's own key, so the effective-access panel below re-reads too. See the note in `AccessTab`.
      onChanged(USER_ADMIN_STRINGS.detailsSaved);
    }
  };

  const withdraw = async () => {
    if (!withdrawing) return;
    const ok = await write.run(() => api.removeMembershipOverride(membershipId, withdrawing.scope));
    if (ok) {
      setWithdrawing(null);
      onChanged(USER_ADMIN_STRINGS.detailsSaved);
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
    {
      key: "withdraw",
      header: t(S.actions),
      /*
        28.16 — the endpoint has existed since 21.2 with no caller, so an exception could be granted here and
        never taken back here. That is the worse half to leave missing: the only remaining way to correct one
        is to change the person's ROLE, which is exactly the over-granting the exception path exists to avoid.
      */
      cell: (r) => (
        <Button variant="ghost" size="sm" onClick={() => setWithdrawing(r)}>
          {t(S.withdraw)}
        </Button>
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <h3 className="panel-h">{t(S.exceptionsPanel)}</h3>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.overrideHelp)}</p>

      <AsyncSection<MembershipDetail> state={detail} isEmpty={() => false} emptyLabel={S.overridesEmpty}>
        {(m) =>
          m.overrides.length === 0 ? (
            <InlineAlert tone="info">{t(S.overridesEmpty)}</InlineAlert>
          ) : (
            <DataTable columns={cols} rows={m.overrides} rowKey={(r) => r.id} caption={t(S.exceptionsPanel)} />
          )
        }
      </AsyncSection>

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

      <Modal
        open={withdrawing !== null}
        onOpenChange={(o) => !o && setWithdrawing(null)}
        title={t(S.withdrawTitle)}
        description={t(S.withdrawBody)}
        footer={
          <>
            {/* `secondary`, not `ghost`: backing out is the recommended action beside a destructive commit
                and must not read as the lighter of the two. */}
            <Button variant="secondary" onClick={() => setWithdrawing(null)}>{t(S.cancel)}</Button>
            <Button variant="danger" loading={write.busy} onClick={() => void withdraw()}>{t(S.withdraw)}</Button>
          </>
        }
      >
        <div className="stack-3">
          {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}
          <p style={{ margin: 0 }}>
            <span className="mono">{withdrawing?.scope}</span> — {withdrawing?.reason}
          </p>
        </div>
      </Modal>
    </Card>
  );
}

/** Reach (§3) — which branches this membership can see, and until when. */
function ReachPanel({ userId, tenantId }: { userId: string; tenantId: string }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const actorName = useActorNames();
  const grants = useAsync<BranchScopeGrant[]>(() => api.branchScopeGrants(userId, tenantId), [userId, tenantId]);
  /*
    28.10 — THE BRANCH, BY NAME.

    This column rendered `r.branchId.slice(0, 8)`: the first eight characters of a uuid. That is worse than
    printing the whole thing. It cannot be copied into anything that would accept it, eight hex characters
    are not guaranteed distinct between two clinics, and it reads as a rendering bug rather than as a value.

    `/branches` is org reference data readable by any authenticated caller, so the lookup costs nothing the
    caller was not already entitled to. When a grant names a branch the directory does not carry (a
    decommissioned clinic still inside an open-ended grant, which is exactly the row a reviewer should catch),
    that is said plainly rather than silently blanked.
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
      <h3 className="panel-h">{t(S.reachPanel)}</h3>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.grantsNote)}</p>
      <AsyncSection<BranchScopeGrant[]> state={grants} isEmpty={(d) => d.length === 0} emptyLabel={S.grantsEmpty}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.grantId} caption={t(S.reachPanel)} />}
      </AsyncSection>
    </Card>
  );
}

/**
 * The effective-access preview — mode 2, rendered verbatim.
 *
 * This panel exists because the three above answer "what has been configured" and none of them answers "what
 * can this person actually do". The set algebra is deny-wins across roles and exceptions, and nobody can run
 * it in their head from three tables.
 */
function EffectivePanel({ membershipId, reloadKey }: { membershipId: string; reloadKey: number }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<EffectiveAccess>(() => api.effectiveAccess(membershipId), [membershipId, reloadKey]);

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <h3 className="panel-h">{t(S.effectivePanel)}</h3>
      <p className="muted" style={{ marginTop: 0 }}>{t(S.previewNote)}</p>
      <EffectiveAccessPreview
        membershipId={membershipId}
        keys={state.data?.keys ?? []}
        loading={state.status === "loading"}
        error={state.status === "error" ? state.error?.message : undefined}
      />
    </Card>
  );
}

/** WHO THIS PERSON IS, and how they get in — details, the lifecycle, and the devices they are signed in on. */
function AccountTab({ user, onSaved }: { user: IdentityUser; onSaved: (m: Localized) => void }) {
  const t = useLoc();
  const [action, setAction] = useState<{ kind: "reset" | "deactivate" | "reactivate"; user: IdentityUser } | null>(null);

  return (
    <div className="stack-3">
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <h3 className="panel-h">{t(S.detailsPanel)}</h3>
        <AccountDetailsForm user={user} onSaved={onSaved} />
      </Card>

      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <h3 className="panel-h">{t(S.signInPanel)}</h3>
        <div className="chip-row" style={{ marginBottom: "var(--sp3)" }}>
          <StatusChip
            kind={user.twoFactorEnabled ? "ok" : "warn"}
            label={`${t(S.twoFactor)}: ${t(user.twoFactorEnabled ? S.mfaOn : S.mfaOff)}`}
          />
        </div>
        {/* 28.7 — an administrator never knows or sets a password. Every route back into an account from
            here is a one-time link to its owner's own address. */}
        <AccountLifecycleActions user={user} onAct={(kind, u) => setAction({ kind, user: u })} />
      </Card>

      <SessionsPanel userId={user.id} />

      <UserActionDialog
        kind={action?.kind ?? null}
        user={action?.user ?? null}
        onClose={() => setAction(null)}
        onDone={onSaved}
      />
    </div>
  );
}

function SessionsPanel({ userId }: { userId: string }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const write = useWrite();
  const [target, setTarget] = useState<AccessSession | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const sessions = useAsync<AccessSession[]>(() => api.accessSessions(userId), [userId, reloadKey]);

  const confirm = async () => {
    if (!target) return;
    const ok = await write.run(() => api.revokeAccessSession(userId, target.sessionId));
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
      <h3 className="panel-h">{t(S.sessionsPanel)}</h3>
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}
      <AsyncSection<AccessSession[]> state={sessions} isEmpty={(d) => d.length === 0} emptyLabel={S.sessionsEmpty}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.sessionId} caption={t(S.sessionsPanel)} />}
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

export const USERS_AND_ACCESS_STRINGS = S;
