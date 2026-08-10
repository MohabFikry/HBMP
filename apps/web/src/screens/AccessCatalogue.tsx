import { useMemo, useState } from "react";
import {
  Button,
  Card,
  DataTable,
  Icon,
  InlineAlert,
  InputField,
  Modal,
  SearchField,
  StatusChip,
  Tabs,
  TextareaField,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, RoleBinding, RoleCatalogEntry, ScopeCatalogEntry, SodConflict } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite } from "../api/useWrite";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

/**
 * Phase 28.9 — the ACCESS CATALOGUE: every permission the platform has, and the roles built out of them.
 *
 * ==========================================================================================================
 * WHY THIS SCREEN EXISTS
 * ==========================================================================================================
 * Permissions have been data since 17.1 — rows in `identity.scope` — and until now nothing listed them. An
 * administrator could assign a role and could grant an exception naming a key, but had no way to discover
 * what keys exist or what any of them means. That leaves exactly one workable strategy in front of a real
 * person with an unusual job: give them the nearest BIGGER role.
 *
 * Which is how least privilege actually dies. Not by anyone arguing against it — by the alternative being
 * unavailable at the moment of the decision. This screen and the role designer beside it are the
 * alternative.
 *
 * ==========================================================================================================
 * WHAT IT IS NOT
 * ==========================================================================================================
 * Reading this page grants nothing and discloses nothing beyond the vocabulary the token contract already
 * publishes. The refusals it renders — SoD conflicts, service-only keys — are the SERVER's; every one of
 * them is re-decided on the write, and `UiGatingIsCosmeticTests` hand-crafts the request behind each hidden
 * affordance to prove the API refuses it too.
 */

const S = {
  title: { en: "Access Catalogue", ar: "دليل الصلاحيات" },
  lede: {
    en: "Every permission in the system, the roles built from them, and where the duties must stay apart.",
    ar: "كل صلاحية في النظام، والأدوار المبنية منها، والمواضع التي يجب أن تبقى فيها المهام منفصلة.",
  },

  tabsLabel: { en: "Access catalogue sections", ar: "أقسام دليل الصلاحيات" },
  tabScopes: { en: "Permissions", ar: "الصلاحيات" },
  tabRoles: { en: "Roles", ar: "الأدوار" },
  tabSod: { en: "Separated duties", ar: "المهام المفصولة" },

  searchScopes: { en: "Search permissions by name, domain or description", ar: "ابحث في الصلاحيات بالاسم أو المجال أو الوصف" },
  permission: { en: "Permission", ar: "الصلاحية" },
  domain: { en: "Area", ar: "المجال" },
  meaning: { en: "What it allows", ar: "ما تتيحه" },
  heldBy: { en: "Held by", ar: "تحملها" },
  heldByNone: { en: "No role", ar: "لا يحملها أي دور" },
  flags: { en: "Notes", ar: "ملاحظات" },
  serviceOnly: { en: "Service only", ar: "للأنظمة فقط" },
  serviceOnlyHelp: {
    en: "Granted to machines, never to people.",
    ar: "تُمنح للأنظمة، ولا تُمنح للأشخاص أبدًا.",
  },
  deprecated: { en: "Superseded", ar: "مستبدلة" },
  platformKey: { en: "Platform administration", ar: "إدارة المنصّة" },
  platformKeyHelp: {
    en: "Administrative reach only — never access to patient data.",
    ar: "صلاحية إدارية فقط — لا تمنح الوصول إلى بيانات المرضى.",
  },
  scopesEmpty: { en: "No permissions match this search.", ar: "لا توجد صلاحيات مطابقة." },

  role: { en: "Role", ar: "الدور" },
  roleDesc: { en: "Purpose", ar: "الغرض" },
  tier: { en: "Sensitivity", ar: "الحساسية" },
  permissionCount: { en: "Permissions", ar: "عدد الصلاحيات" },
  origin: { en: "Origin", ar: "المصدر" },
  builtIn: { en: "Built-in", ar: "مدمج" },
  custom: { en: "Yours", ar: "خاص بمؤسستك" },
  rolesEmpty: { en: "No roles.", ar: "لا توجد أدوار." },
  edit: { en: "Edit permissions", ar: "تعديل الصلاحيات" },

  design: { en: "Design a role", ar: "تصميم دور" },
  designHelp: {
    en: "A role is a named set of permissions. It grants what you tick and nothing else — and it adds permissions to the portal its holder already has, rather than opening a new one.",
    ar: "الدور هو مجموعة صلاحيات باسم. يمنح ما تحدده فقط — ويضيف صلاحيات إلى بوابة صاحبه الحالية بدلاً من فتح بوابة جديدة.",
  },
  roleName: { en: "Name", ar: "الاسم" },
  roleNameHelp: {
    en: "Lower-case letters, digits and underscores — e.g. triage_lead. It appears in the audit trail exactly as typed.",
    ar: "حروف صغيرة وأرقام وشرطة سفلية — مثل triage_lead. يظهر في سجل التدقيق كما تكتبه.",
  },
  roleNameInvalid: {
    en: "Use 3–49 characters: lower-case letters, digits and underscores, starting with a letter.",
    ar: "استخدم من 3 إلى 49 حرفًا: حروف صغيرة وأرقام وشرطة سفلية، بادئًا بحرف.",
  },
  purposeHelp: {
    en: "What this role is for, in a sentence. Whoever reviews it in six months will have only this.",
    ar: "الغرض من هذا الدور في جملة. من يراجعه بعد ستة أشهر لن يجد سواها.",
  },
  chooseScopes: { en: "Permissions", ar: "الصلاحيات" },
  chooseScopesRequired: { en: "Choose at least one permission — a role that grants nothing cannot be used.", ar: "اختر صلاحية واحدة على الأقل — الدور الذي لا يمنح شيئًا لا يمكن استخدامه." },
  selectedCount: { en: "{n} selected", ar: "{n} محددة" },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  saved: { en: "Saved.", ar: "تم الحفظ." },
  sodRefused: {
    en: "Separation of duties refuses this combination. The two permissions named below must never be held by one person.",
    ar: "الفصل بين المهام يمنع هذا الجمع. الصلاحيتان أدناه يجب ألّا يحملهما شخص واحد.",
  },

  sodLede: {
    en: "Pairs one person must never hold together. Enforced on every role, every assignment and every exception — this table is what refuses them.",
    ar: "أزواج يجب ألّا يحملها شخص واحد. تُطبَّق على كل دور وكل إسناد وكل استثناء — وهذا الجدول هو ما يرفضها.",
  },
  roleA: { en: "Role", ar: "الدور" },
  roleB: { en: "Conflicts with", ar: "يتعارض مع" },
  reason: { en: "Risk", ar: "المخاطرة" },
  sodEmpty: { en: "No separated duties defined.", ar: "لا توجد مهام مفصولة." },

  tabBindings: { en: "Assignments", ar: "الإسنادات" },
  bindingsLede: {
    en: "Who currently holds which role, and when each grant is due to be re-approved. The roster answers what one person holds; this answers who holds one thing.",
    ar: "من يحمل أي دور حاليًا، وموعد إعادة اعتماد كل منح. القائمة تجيب عمّا يحمله شخص واحد، وهذا يجيب عمّن يحمل شيئًا واحدًا.",
  },
  bindingsEmpty: { en: "No role bindings in this tenant.", ar: "لا توجد أدوار مُسندة في هذا المستأجر." },
  subject: { en: "Subject", ar: "المستخدم" },
  bindingScope: { en: "Scope", ar: "النطاق" },
  bindingStatus: { en: "Status", ar: "الحالة" },
  reviewDue: { en: "Review due", ar: "موعد المراجعة" },
} satisfies Record<string, Localized>;

export function AccessCatalogue() {
  const t = useLoc();
  const [tab, setTab] = useState("scopes");
  const [reloadKey, setReloadKey] = useState(0);

  return (
    <>
      <PageHeader title={t(S.title)} />
      <p className="lede">{t(S.lede)}</p>
      <Tabs
        aria-label={t(S.tabsLabel)}
        value={tab}
        onValueChange={setTab}
        items={[
          { value: "scopes", label: t(S.tabScopes), content: <ScopesTab /> },
          {
            value: "roles",
            label: t(S.tabRoles),
            content: <RolesTab reloadKey={reloadKey} onChanged={() => setReloadKey((k) => k + 1)} />,
          },
          { value: "bindings", label: t(S.tabBindings), content: <BindingsTab /> },
          { value: "sod", label: t(S.tabSod), content: <SodTab /> },
        ]}
      />
    </>
  );
}

/** Every permission in the system, searchable. The reference half of the screen. */
function ScopesTab() {
  const api = useApi();
  const t = useLoc();
  const [query, setQuery] = useState("");
  const scopes = useAsync<ScopeCatalogEntry[]>(() => api.scopeCatalog(), []);

  const cols: Column<ScopeCatalogEntry>[] = [
    {
      key: "name",
      header: t(S.permission),
      cell: (s) => <code className="scope-key">{s.name}</code>,
      sortable: true,
      sortValue: (s) => s.name,
    },
    { key: "domain", header: t(S.domain), cell: (s) => s.domain, sortable: true, sortValue: (s) => s.domain },
    { key: "meaning", header: t(S.meaning), cell: (s) => s.description ?? "—" },
    {
      // The question an administrator has in front of a permission is "who has this already". Without it,
      // deciding whether a new role needs a key is guesswork, and the safe guess is always to include it.
      key: "heldBy",
      header: t(S.heldBy),
      cell: (s) => (s.heldBy.length ? s.heldBy.join(", ") : <span className="muted">{t(S.heldByNone)}</span>),
    },
    {
      key: "flags",
      header: t(S.flags),
      cell: (s) => (
        <span className="chip-row">
          {s.serviceOnly && <StatusChip kind="neu" label={t(S.serviceOnly)} />}
          {s.deprecated && (
            <StatusChip kind="warn" label={s.replacedBy ? `${t(S.deprecated)} → ${s.replacedBy}` : t(S.deprecated)} />
          )}
          {s.isPlatformAdminKey && <StatusChip kind="info" label={t(S.platformKey)} />}
          {!s.serviceOnly && !s.deprecated && !s.isPlatformAdminKey && <span className="muted">—</span>}
        </span>
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <SearchField
        aria-label={t(S.searchScopes)}
        placeholder={t(S.searchScopes)}
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
        style={{ marginBottom: "var(--sp3)" }}
      />
      <AsyncSection<ScopeCatalogEntry[]> state={scopes} isEmpty={(d) => d.length === 0} emptyLabel={S.scopesEmpty}>
        {(all) => {
          const q = query.trim().toLowerCase();
          const rows = q
            ? all.filter(
                (s) =>
                  s.name.toLowerCase().includes(q) ||
                  s.domain.toLowerCase().includes(q) ||
                  (s.description ?? "").toLowerCase().includes(q),
              )
            : all;
          return <DataTable columns={cols} rows={rows} rowKey={(s) => s.name} caption={t(S.tabScopes)} />;
        }}
      </AsyncSection>
    </Card>
  );
}

/** The roles, and the designer that makes new ones. */
function RolesTab({ reloadKey, onChanged }: { reloadKey: number; onChanged: () => void }) {
  const api = useApi();
  const t = useLoc();
  const roles = useAsync<RoleCatalogEntry[]>(() => api.roleCatalog(), [reloadKey]);
  const [designing, setDesigning] = useState(false);
  const [editing, setEditing] = useState<RoleCatalogEntry | null>(null);

  const cols: Column<RoleCatalogEntry>[] = [
    { key: "role", header: t(S.role), cell: (r) => <code className="scope-key">{r.name}</code>, sortable: true, sortValue: (r) => r.name },
    { key: "desc", header: t(S.roleDesc), cell: (r) => r.description ?? "—" },
    { key: "tier", header: t(S.tier), cell: (r) => <StatusChip kind="neu" label={r.sensitivityTier} /> },
    // `numeric` on the COLUMN rather than `.tnum` on the value: the class sets the figure width and leaves
    // the column ragged, so a table of counts does not line up (table-numeric-columns guards this).
    { key: "count", header: t(S.permissionCount), cell: (r) => r.scopes.length, numeric: true, sortable: true, sortValue: (r) => r.scopes.length },
    {
      // Built-in and custom roles are edited on the same terms but they are not the same KIND of thing: one
      // is platform policy this tenant is adjusting, the other is this tenant's own invention.
      key: "origin",
      header: t(S.origin),
      cell: (r) => <StatusChip kind={r.custom ? "info" : "neu"} label={r.custom ? t(S.custom) : t(S.builtIn)} />,
    },
    {
      key: "edit",
      header: "",
      cell: (r) => (
        <Button variant="ghost" size="sm" leadingIcon={<Icon name="pen" />} onClick={() => setEditing(r)}>
          {t(S.edit)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <div className="pagehead-actions" style={{ marginBottom: "var(--sp3)" }}>
          <Button variant="primary" onClick={() => setDesigning(true)}>
            {t(S.design)}
          </Button>
        </div>
        <AsyncSection<RoleCatalogEntry[]> state={roles} isEmpty={(d) => d.length === 0} emptyLabel={S.rolesEmpty}>
          {(list) => <DataTable columns={cols} rows={list} rowKey={(r) => r.name} caption={t(S.tabRoles)} />}
        </AsyncSection>
      </Card>

      <RoleDialog
        open={designing}
        role={null}
        onClose={() => setDesigning(false)}
        onSaved={() => {
          setDesigning(false);
          onChanged();
        }}
      />
      <RoleDialog
        open={editing !== null}
        role={editing}
        onClose={() => setEditing(null)}
        onSaved={() => {
          setEditing(null);
          onChanged();
        }}
      />
    </>
  );
}

/**
 * Design a role, or change what an existing one grants.
 *
 * One dialog for both, because they are one act: choose a name and choose permissions. Splitting them would
 * mean two permission pickers with two chances to diverge, and the picker is the part that has to be right.
 */
function RoleDialog({
  open,
  role,
  onClose,
  onSaved,
}: {
  open: boolean;
  role: RoleCatalogEntry | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const scopes = useAsync<ScopeCatalogEntry[]>(() => api.scopeCatalog(), [open]);

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [tier, setTier] = useState("T2");
  const [chosen, setChosen] = useState<string[]>([]);
  const [touched, setTouched] = useState(false);
  const [seeded, setSeeded] = useState<string | null>(null);

  // Seed from the role being edited, once per opening. A `useEffect` on `role` would fight the user's own
  // edits on every re-render of the parent; keying off the identity we have already seeded does not.
  const seedKey = open ? (role?.name ?? "__new__") : null;
  if (seedKey !== seeded) {
    setSeeded(seedKey);
    setName(role?.name ?? "");
    setDescription(role?.description ?? "");
    setTier(role?.sensitivityTier ?? "T2");
    setChosen(role ? [...role.scopes] : []);
    setTouched(false);
    write.reset();
  }

  const nameValid = /^[a-z][a-z0-9_]{2,48}$/.test(name);
  const editing = role !== null;

  const byDomain = useMemo(() => {
    const map = new Map<string, ScopeCatalogEntry[]>();
    for (const s of scopes.data ?? []) {
      const list = map.get(s.domain) ?? [];
      list.push(s);
      map.set(s.domain, list);
    }
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [scopes.data]);

  async function save() {
    setTouched(true);
    if (!editing && !nameValid) return;
    if (chosen.length === 0) return;
    const ok = await write.run(async () => {
      if (editing) await api.setRoleScopes(role.name, chosen);
      else await api.createRole({ name, scopes: chosen, description: description || undefined, sensitivityTier: tier });
    });
    if (ok) onSaved();
  }

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={editing ? `${t(S.edit)} — ${role.name}` : t(S.design)}
      description={t(S.designHelp)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t(S.cancel)}
          </Button>
          <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={write.busy} onClick={() => void save()}>
            {t(S.save)}
          </Button>
        </>
      }
    >
      <div className="stack-3">
        {write.error && (
          <InlineAlert tone="bad">
            {/* An SoD refusal is not a validation error and must not read as one: it says a COMBINATION is
                forbidden, which is a different instruction from "fix this field". */}
            {write.error.status === 409 ? t(S.sodRefused) : t(write.error.message)}
          </InlineAlert>
        )}

        {!editing && (
          <>
            <InputField
              label={t(S.roleName)}
              help={t(S.roleNameHelp)}
              error={touched && !nameValid ? t(S.roleNameInvalid) : undefined}
              value={name}
              onChange={(e) => setName(e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, ""))}
            />
            <TextareaField
              label={t(S.roleDesc)}
              help={t(S.purposeHelp)}
              rows={2}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
            />
            <fieldset className="tier-choice">
              <legend>{t(S.tier)}</legend>
              {["T1", "T2", "T3", "T4"].map((x) => (
                <label key={x} className="tier-option">
                  <input type="radio" name="tier" value={x} checked={tier === x} onChange={() => setTier(x)} />
                  <span>{x}</span>
                </label>
              ))}
            </fieldset>
          </>
        )}

        <fieldset className="scope-picker">
          <legend>
            {t(S.chooseScopes)} — {t(S.selectedCount).replace("{n}", String(chosen.length))}
          </legend>
          {touched && chosen.length === 0 && (
            <InlineAlert tone="bad">
              {t(S.chooseScopesRequired)}
            </InlineAlert>
          )}
          <div className="scope-picker-list mrs-scroll">
            {byDomain.map(([domain, list]) => (
              <div key={domain} className="scope-picker-group">
                <h4 className="scope-picker-domain">{domain}</h4>
                {list.map((s) => (
                  <label
                    key={s.name}
                    className="scope-picker-item"
                    /* A service-only key is not merely discouraged here — the server refuses it, so offering
                       a tickable box would be offering a control that fails. The reason is stated rather
                       than left as a mystery disabled row. */
                    title={s.serviceOnly ? t(S.serviceOnlyHelp) : (s.description ?? undefined)}
                  >
                    <input
                      type="checkbox"
                      disabled={s.serviceOnly}
                      checked={chosen.includes(s.name)}
                      /* `checked` is read HERE and not inside the updater. React nulls a synthetic
                         event's `currentTarget` once dispatch returns, and a state updater runs after that
                         — so the closure form throws "Cannot read properties of null". */
                      onChange={(e) => {
                        const on = e.currentTarget.checked;
                        setChosen((prev) => (on ? [...prev, s.name] : prev.filter((x) => x !== s.name)));
                      }}
                    />
                    <span className="scope-picker-name">
                      <code className="scope-key">{s.name}</code>
                      {s.serviceOnly && <StatusChip kind="neu" label={t(S.serviceOnly)} />}
                      {s.deprecated && <StatusChip kind="warn" label={t(S.deprecated)} />}
                      {s.isPlatformAdminKey && <StatusChip kind="info" label={t(S.platformKey)} />}
                    </span>
                    {s.description && <span className="scope-picker-desc">{s.description}</span>}
                  </label>
                ))}
              </div>
            ))}
          </div>
        </fieldset>
      </div>
    </Modal>
  );
}

/**
 * Role BINDINGS and their recertification dates — who currently holds what, and when it must be re-approved.
 *
 * Carried over from the "Users & Roles" screen 28.8 merged away. It is the only part of that screen not
 * superseded: the roster answers "what does this person hold", and this answers the reviewer's question,
 * which is the opposite direction — "who holds this, and is any of it overdue". Dropping it in the merge
 * would have quietly removed the recertification dates from the product.
 */
function BindingsTab() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const rows = useAsync<RoleBinding[]>(() => api.accessMatrix(), []);

  const cols: Column<RoleBinding>[] = [
    // The subject is a TOKEN, not a name: this is admin-service's projection of who holds what, and it
    // deliberately does not carry an identity. Pairing a name with a role here would make a governance table
    // into a directory of staff.
    { key: "subject", header: t(S.subject), cell: (r) => <span className="mono">{r.subjectToken}</span>, sortable: true, sortValue: (r) => r.subjectToken },
    { key: "role", header: t(S.role), cell: (r) => <StatusChip kind="neu" label={r.role} />, sortable: true, sortValue: (r) => r.role },
    { key: "scope", header: t(S.bindingScope), cell: (r) => r.scope || "—" },
    { key: "tier", header: t(S.tier), cell: (r) => <StatusChip kind="neu" label={r.tier} /> },
    { key: "status", header: t(S.bindingStatus), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "review",
      header: t(S.reviewDue),
      cell: (r) => (r.reviewDueAt ? <span className="tnum">{fmt.date(r.reviewDueAt)}</span> : <span className="muted">—</span>),
      sortable: true,
      sortValue: (r) => r.reviewDueAt ?? "",
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <p className="lede">{t(S.bindingsLede)}</p>
      <AsyncSection<RoleBinding[]> state={rows} isEmpty={(d) => d.length === 0} emptyLabel={S.bindingsEmpty}>
        {(list) => (
          <DataTable columns={cols} rows={list} rowKey={(r) => r.id} caption={t(S.tabBindings)} />
        )}
      </AsyncSection>
    </Card>
  );
}

/** The separated duties, which is what refuses a role that tries to hold both halves of one. */
function SodTab() {
  const api = useApi();
  const t = useLoc();
  const rows = useAsync<SodConflict[]>(() => api.sodMatrix(), []);

  const cols: Column<SodConflict>[] = [
    { key: "a", header: t(S.roleA), cell: (r) => r.roleA, sortable: true, sortValue: (r) => r.roleA },
    { key: "b", header: t(S.roleB), cell: (r) => r.roleB },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <p className="lede">{t(S.sodLede)}</p>
      <AsyncSection<SodConflict[]> state={rows} isEmpty={(d) => d.length === 0} emptyLabel={S.sodEmpty}>
        {(list) => (
          <DataTable columns={cols} rows={list} rowKey={(r) => `${r.roleA}|${r.roleB}`} caption={t(S.tabSod)} />
        )}
      </AsyncSection>
    </Card>
  );
}
