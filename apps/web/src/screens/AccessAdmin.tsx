import { useCallback, useState } from "react";
import {
  Button,
  Card,
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
  BranchScopeGrant,
  EffectiveAccess,
  Localized,
  MembershipDetail,
  MembershipOverride,
  MembershipRow,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite } from "../api/useWrite";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { EffectiveAccessPreview } from "./EffectiveAccessPreview";

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
  rosterEmpty: { en: "No memberships in this tenant.", ar: "لا توجد عضويات في هذا المستأجر." },
  search: { en: "Search by name or username", ar: "ابحث بالاسم أو اسم المستخدم" },
  person: { en: "Person", ar: "الشخص" },
  tenant: { en: "Organisation", ar: "المؤسسة" },
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
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  const rows = useAsync<MembershipRow[]>(() => api.memberships(undefined, undefined, query || undefined), [query]);

  if (selected) return <MembershipDetailScreen membershipId={selected} onBack={() => setSelected(null)} />;

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
    { key: "tenant", header: t(S.tenant), cell: (r) => <span className="tnum">{r.tenantId}</span> },
    { key: "roles", header: t(S.roles), cell: (r) => (r.roles.length ? r.roles.map((x) => x.name).join(", ") : "—") },
    { key: "level", header: t(S.level), cell: (r) => <StatusChip kind="neu" label={levelLabel(r.level, t)} /> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
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
      header: "",
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
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <SearchField
          aria-label={t(S.search)}
          placeholder={t(S.search)}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          style={{ marginBottom: "var(--sp3)" }}
        />
        <AsyncSection<MembershipRow[]> state={rows} isEmpty={(d) => d.length === 0} emptyLabel={S.rosterEmpty}>
          {(list) => (
            <DataTable columns={cols} rows={list} rowKey={(r) => r.membershipId} caption={t(S.rosterTitle)} />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/** One membership, in five tabs: authority, exceptions, reach, sessions, and what it all adds up to. */
export function MembershipDetailScreen({ membershipId, onBack }: { membershipId: string; onBack?: () => void }) {
  const api = useApi();
  const t = useLoc();
  const [tab, setTab] = useState("roles");
  const [reloadKey, setReloadKey] = useState(0);
  const detail = useAsync<MembershipDetail>(() => api.membership(membershipId), [membershipId, reloadKey]);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);

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
              <p className="muted" style={{ margin: 0 }}>
                {m.tenantId} · <StatusChip kind={m.status.kind} label={t(m.status.label)} /> ·{" "}
                {levelLabel(m.level, t)}
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
    { key: "role", header: t(S.role), cell: (r) => r.name },
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
    { key: "scope", header: t(S.scope), cell: (r) => <span className="mono">{r.scope}</span> },
    {
      key: "effect",
      header: t(S.effect),
      // Deny is `bad` and Allow is `ok`: the two are opposite decisions and must never be told apart by
      // reading the word alone (four-cue status, 21-accessibility).
      cell: (r) => <StatusChip kind={r.effect === "Deny" ? "bad" : "ok"} label={t(r.effect === "Deny" ? S.deny : S.allow)} />,
    },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason },
    { key: "grantedBy", header: t(S.grantedBy), cell: (r) => <span className="muted">{r.grantedBy ?? "—"}</span> },
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

        <InputField
          label={t(S.scope)}
          value={scope}
          error={scopeError}
          onChange={(e) => setScope(e.currentTarget.value)}
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
  const grants = useAsync<BranchScopeGrant[]>(
    () => api.branchScopeGrants(membership.userId, membership.tenantId),
    [membership.userId, membership.tenantId],
  );

  const cols: Column<BranchScopeGrant>[] = [
    {
      key: "branch",
      header: t(S.branch),
      cell: (r) => (
        <span>
          <span className="tnum">{r.branchId.slice(0, 8)}</span>
          {r.isHome ? <> <StatusChip kind="info" label={t(S.home)} /></> : null}
        </span>
      ),
    },
    { key: "from", header: t(S.from), cell: (r) => <span className="tnum">{fmt.date(r.validFrom)}</span> },
    {
      key: "until",
      header: t(S.until),
      cell: (r) =>
        r.validUntil ? <span className="tnum">{fmt.date(r.validUntil)}</span> : <span className="muted">{t(S.openEnded)}</span>,
    },
    { key: "grantedBy", header: t(S.grantedBy), cell: (r) => <span className="muted">{r.grantedBy ?? "—"}</span> },
    // The reason is a column, not a tooltip: "covering Alexandria for October" is what makes an expiring
    // grant reviewable, and a reviewer working down a list will not hover every row.
    { key: "reason", header: t(S.reason), cell: (r) => r.grantedReason ?? "—" },
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
    { key: "device", header: t(S.device), cell: (r) => r.device },
    { key: "signedIn", header: t(S.signedIn), cell: (r) => <span className="tnum">{fmt.dateTime(r.createdAt)}</span> },
    {
      key: "lastSeen",
      header: t(S.lastSeen),
      cell: (r) => <span className="tnum">{r.lastSeenAt ? fmt.dateTime(r.lastSeenAt) : "—"}</span>,
    },
    {
      key: "revoke",
      header: "",
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
