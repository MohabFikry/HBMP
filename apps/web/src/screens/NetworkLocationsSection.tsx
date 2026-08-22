import { useCallback, useState } from "react";
import {
  Button, Card, CheckboxField, ComboboxField, DataTable, Icon, InlineAlert, InputField, Modal, StatusChip,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, ProviderLocationAdmin, ProviderSummary, ProviderUserView } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerTheNetwork } from "../authz/permissions";
import { writeErrorMessage } from "../api/writeError";
import { AsyncSection, PageHeader, fillLocalized, useLoc } from "./_shared";
import { RecordActions, ReasonDialog } from "./AdminRecordControls";
import { useIdempotencyKey } from "./PolicyPanels";
import { NetworkHistoryModal, ProviderScope, networkApi, useDate, useLoad } from "./NetworkAdminShared";
import { useFormat } from "../i18n/useFormat";

/**
 * Phase 19.9 — Locations & users (design 58).
 *
 * ============================================================================================================
 * THE PRIMARY LOCATION COULD NEVER BE CHANGED
 * ============================================================================================================
 * Exactly one primary location per provider is a partial-unique index, enforced since migration 0001. The
 * only write the service offered was "add a location", so adding a second primary answered 409 — and there
 * was no demote, no edit and no deactivate. A provider whose head office moved could not be corrected at all,
 * and a primary location is not cosmetic: activation is gated on having one, and it is the address referrals
 * are sent to.
 *
 * Promotion is now one transaction that demotes first. The order matters: the reverse violates the index, and
 * two separate commits can leave the provider with no primary at all — which silently fails its own
 * activation check while the directory goes on saying Active.
 *
 * ============================================================================================================
 * DEACTIVATION KEEPS THE ROW AND THE REASON
 * ============================================================================================================
 * A closed location stays in this list, greyed and labelled, with why and when. Routing has already sent
 * patients to that address; "it is gone" and "it closed in March when the lease ended" are different answers
 * to somebody holding an appointment card for it.
 *
 * ============================================================================================================
 * REVOKING ONE ACCOUNT
 * ============================================================================================================
 * Until now the only way to take an account away from a provider was to SUSPEND the whole provider, which
 * revokes every account they hold and stops routing to them — an outsized answer to "this person left".
 */

const S = {
  title: { en: "Locations & users", ar: "المواقع والمستخدمون" },
  subtitle: {
    en: "Where a provider delivers care, and who from their organisation can sign in.",
    ar: "أين يقدّم مقدم الخدمة الرعاية، ومن في مؤسسته يمكنه تسجيل الدخول.",
  },
  // ── locations ───────────────────────────────────────────────────────────────────────────────────────────
  locations: { en: "Locations", ar: "المواقع" },
  name: { en: "Name", ar: "الاسم" },
  governorate: { en: "Governorate", ar: "المحافظة" },
  address: { en: "Address", ar: "العنوان" },
  standing: { en: "Standing", ar: "الحالة" },
  primary: { en: "Primary", ar: "رئيسي" },
  open: { en: "Open", ar: "مفتوح" },
  closed: { en: "Closed", ar: "مغلق" },
  noLocations: { en: "No locations recorded for this provider.", ar: "لا توجد مواقع مسجلة لمقدم الخدمة." },
  newLocation: { en: "Add a location", ar: "إضافة موقع" },
  editLocation: { en: "Edit location", ar: "تعديل الموقع" },
  makePrimary: { en: "Make primary", ar: "تعيينه رئيسيًا" },
  deactivate: { en: "Close this location", ar: "إغلاق هذا الموقع" },
  reactivate: { en: "Reopen this location", ar: "إعادة فتح الموقع" },
  locationHistory: { en: "Location history", ar: "سجل الموقع" },
  isPrimaryField: { en: "This is the provider's primary location", ar: "هذا هو الموقع الرئيسي لمقدم الخدمة" },
  isPrimaryHint: {
    en: "The address referrals are sent to. A provider cannot be activated without one, and only one location can hold it.",
    ar: "العنوان الذي تُرسَل إليه الإحالات. لا يمكن تفعيل مقدم الخدمة بدونه، ولا يحمله سوى موقع واحد.",
  },
  primaryTitle: { en: "Make {0} the primary location?", ar: "تعيين {0} موقعًا رئيسيًا؟" },
  primaryBody: {
    en: "Referrals and the directory will point here instead. The location that holds it now is demoted in the same step — a provider is never left without one.",
    ar: "ستشير الإحالات والدليل إلى هنا بدلًا من ذلك. ويُخفَّض الموقع الحالي في الخطوة نفسها — فلا يُترك مقدم الخدمة بلا موقع رئيسي.",
  },
  primaryConfirm: { en: "Make it primary", ar: "تعيينه رئيسيًا" },
  deactivateTitle: { en: "Close {0}?", ar: "إغلاق {0}؟" },
  deactivateBody: {
    en: "It stops being offered for booking and routing. The record and your reason are kept, so an appointment already made there can still be explained.",
    ar: "يتوقف عرضه للحجز والتوجيه. ويُحفَظ السجل وسببك، حتى يظل بالإمكان تفسير موعد سبق حجزه فيه.",
  },
  reactivateTitle: { en: "Reopen {0}?", ar: "إعادة فتح {0}؟" },
  reactivateBody: {
    en: "It becomes bookable again. It does NOT become the primary location — that would move the provider's official address as a side effect.",
    ar: "يصبح قابلًا للحجز مجددًا. لكنه لا يصبح الموقع الرئيسي — فذلك ينقل العنوان الرسمي لمقدم الخدمة كأثر جانبي.",
  },
  closedOn: { en: "Closed {0}", ar: "أُغلق {0}" },
  // ── the form ────────────────────────────────────────────────────────────────────────────────────────────
  nameHint: { en: "What staff and beneficiaries call this site.", ar: "الاسم الذي يعرفه به الموظفون والمستفيدون." },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  needName: { en: "A name is required.", ar: "الاسم مطلوب." },
  // ── users ───────────────────────────────────────────────────────────────────────────────────────────────
  users: { en: "Provider users", ar: "مستخدمو مقدم الخدمة" },
  usersHint: {
    en: "Accounts belonging to this provider's own staff. Suspending the provider revokes all of them; this revokes one.",
    ar: "حسابات موظفي مقدم الخدمة. إيقاف مقدم الخدمة يلغي جميعها؛ وهذا يلغي حسابًا واحدًا.",
  },
  noUsers: { en: "No accounts provisioned for this provider.", ar: "لا توجد حسابات لمقدم الخدمة." },
  subject: { en: "Account", ar: "الحساب" },
  role: { en: "Role", ar: "الدور" },
  since: { en: "Since", ar: "منذ" },
  active: { en: "Active", ar: "نشط" },
  revoked: { en: "Revoked", ar: "ملغى" },
  addUser: { en: "Add an account", ar: "إضافة حساب" },
  revoke: { en: "Revoke", ar: "إلغاء" },
  revokeTitle: { en: "Revoke {0}?", ar: "إلغاء {0}؟" },
  revokeBody: {
    en: "The account can no longer sign in. The record is kept — an action they took last month still has to be attributable.",
    ar: "لن يتمكن الحساب من تسجيل الدخول. ويُحفَظ السجل — فما فعله الشهر الماضي يجب أن يظل منسوبًا إليه.",
  },
  subjectField: { en: "Identity subject", ar: "معرّف الهوية" },
  subjectHint: {
    en: "The identity account this provider role attaches to. It must already exist — this grants access, it does not create a person.",
    ar: "حساب الهوية الذي يُربَط به هذا الدور. يجب أن يكون موجودًا مسبقًا — هذا يمنح الوصول ولا يُنشئ شخصًا.",
  },
  roleField: { en: "Role at this provider", ar: "الدور لدى مقدم الخدمة" },
  readOnly: {
    en: "Locations and accounts are administered by Mersal's Network Team. You are seeing your own record.",
    ar: "تدير المواقع والحسابات إدارة الشبكة في مرسال. أنت ترى سجلك الخاص.",
  },
} satisfies Record<string, Localized>;

const HISTORY_LABELS: Record<string, Localized> = {
  name: S.name,
  governorate: S.governorate,
  address: S.address,
  is_primary: S.primary,
  is_deleted: S.closed,
};

export function NetworkLocations() {
  const api = useApi();
  const t = useLoc();
  const { session } = useAuth();
  const mayWrite = mayAdministerTheNetwork(session?.issuerRoles);
  const providers = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  const [picked, setPicked] = useState<ProviderSummary | null>(null);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <p className="pol-muted">{t(S.subtitle)}</p>
      {!mayWrite && <InlineAlert tone="info">{t(S.readOnly)}</InlineAlert>}
      <AsyncSection state={providers} isEmpty={() => false} emptyLabel={S.noLocations}>
        {(rows) => (
          <ProviderScope providers={rows} picked={picked} onPick={setPicked} title={t(S.title)}>
            {(p) => (
              <>
                <LocationsPanel provider={p} mayWrite={mayWrite} />
                <UsersPanel provider={p} mayWrite={mayWrite} />
              </>
            )}
          </ProviderScope>
        )}
      </AsyncSection>
    </div>
  );
}

// ── Locations ───────────────────────────────────────────────────────────────────────────────────────────

function LocationsPanel({ provider, mayWrite }: { provider: ProviderSummary; mayWrite: boolean }) {
  const t = useLoc();
  const date = useDate();
  const [form, setForm] = useState<{ mode: "create" } | { mode: "edit"; location: ProviderLocationAdmin } | null>(null);
  const [promoting, setPromoting] = useState<ProviderLocationAdmin | null>(null);
  const [closing, setClosing] = useState<ProviderLocationAdmin | null>(null);
  const [reopening, setReopening] = useState<ProviderLocationAdmin | null>(null);
  const [historyFor, setHistoryFor] = useState<ProviderLocationAdmin | null>(null);

  const load = useCallback(() => networkApi.locations(provider.id), [provider.id]);
  const [locations, reload] = useLoad(load, [provider.id]);

  const columns: Column<ProviderLocationAdmin>[] = [
    { key: "name", header: t(S.name), cell: (l) => l.name, sortable: true, sortValue: (l) => l.name },
    { key: "gov", header: t(S.governorate), cell: (l) => l.governorate ?? "—", sortable: true, sortValue: (l) => l.governorate ?? "" },
    { key: "addr", header: t(S.address), cell: (l) => l.address ?? "—" },
    {
      key: "standing", header: t(S.standing),
      cell: (l) => (
        <div className="pay-chips">
          {/* Four cues, not colour alone: the primary chip is a solid pill, a closed one a square badge. */}
          {l.isPrimary && <StatusChip kind="ok" label={t(S.primary)} />}
          <StatusChip kind={l.isDeleted ? "bad" : "neu"} label={t(l.isDeleted ? S.closed : S.open)} />
          {l.isDeleted && l.deactivatedAt && (
            <span className="pol-muted">{fillLocalized(S.closedOn, date(l.deactivatedAt)).en}</span>
          )}
        </div>
      ),
      sortable: true, sortValue: (l) => (l.isPrimary ? "0" : l.isDeleted ? "2" : "1"),
    },
    {
      key: "actions", header: "",
      cell: (l) => (
        <RecordActions
          onHistory={() => setHistoryFor(l)}
          onEdit={mayWrite && !l.isDeleted ? () => setForm({ mode: "edit", location: l }) : undefined}
          editLabel={S.editLocation}
          status={
            !mayWrite
              ? undefined
              : l.isDeleted
                ? { label: S.reactivate, icon: "check2", onClick: () => setReopening(l) }
                : { label: S.deactivate, icon: "cross", onClick: () => setClosing(l) }
          }
        >
          {/* Offered only where it does something: the current primary has nothing to be promoted to, and a
              closed location cannot hold the address referrals are sent to. */}
          {mayWrite && !l.isPrimary && !l.isDeleted && (
            <Button variant="ghost" size="sm" aria-label={t(S.makePrimary)} title={t(S.makePrimary)} onClick={() => setPromoting(l)}>
              <Icon name="swap" />
            </Button>
          )}
        </RecordActions>
      ),
    },
  ];

  return (
    <Card as="section">
      <div className="pol-stack">
        <div className="screen-toolbar">
          <h3 style={{ margin: 0 }}>{t(S.locations)}</h3>
          {mayWrite && (
            <Button variant="secondary" leadingIcon={<Icon name="plus" />} onClick={() => setForm({ mode: "create" })}>
              {t(S.newLocation)}
            </Button>
          )}
        </div>
        {/* Bare table on purpose: this is ONE provider's sites, which is a handful, and a pager over four
            rows hides nothing while adding a control to operate. */}
        <DataTable
          columns={columns}
          rows={locations ?? []}
          rowKey={(l) => l.locationId}
          caption={t(S.locations)}
          emptyLabel={t(S.noLocations)}
        />
      </div>

      {form && (
        <LocationForm
          providerId={provider.id}
          location={form.mode === "edit" ? form.location : null}
          hasPrimary={(locations ?? []).some((l) => l.isPrimary && !l.isDeleted)}
          onClose={() => setForm(null)}
          onSaved={() => { setForm(null); reload(); }}
        />
      )}

      {promoting && (
        <ReasonDialog
          title={fillLocalized(S.primaryTitle, promoting.name)}
          body={S.primaryBody}
          confirmLabel={S.primaryConfirm}
          onConfirm={async (_reason, key) => { await networkApi.makeLocationPrimary(provider.id, promoting.locationId, key); }}
          onClose={() => setPromoting(null)}
          onDone={() => { setPromoting(null); reload(); }}
        />
      )}

      {closing && (
        <ReasonDialog
          title={fillLocalized(S.deactivateTitle, closing.name)}
          body={S.deactivateBody}
          confirmLabel={S.deactivate}
          onConfirm={async (reason, key) => { await networkApi.deactivateLocation(provider.id, closing.locationId, reason, key); }}
          onClose={() => setClosing(null)}
          onDone={() => { setClosing(null); reload(); }}
        />
      )}

      {reopening && (
        <ReasonDialog
          title={fillLocalized(S.reactivateTitle, reopening.name)}
          body={S.reactivateBody}
          confirmLabel={S.reactivate}
          onConfirm={async (reason, key) => { await networkApi.reactivateLocation(provider.id, reopening.locationId, reason, key); }}
          onClose={() => setReopening(null)}
          onDone={() => { setReopening(null); reload(); }}
        />
      )}

      {historyFor && (
        <NetworkHistoryModal
          title={S.locationHistory}
          labels={HISTORY_LABELS}
          load={() => networkApi.locationHistory(provider.id, historyFor.locationId)}
          onClose={() => setHistoryFor(null)}
        />
      )}
    </Card>
  );
}

function LocationForm({
  providerId, location, hasPrimary, onClose, onSaved,
}: {
  providerId: string;
  location: ProviderLocationAdmin | null;
  hasPrimary: boolean;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [name, setName] = useState(location?.name ?? "");
  const [governorate, setGovernorate] = useState(location?.governorate ?? "");
  const [address, setAddress] = useState(location?.address ?? "");
  // On CREATE only. Promotion of an existing location is its own action with its own confirmation, because
  // it demotes another row — a checkbox in an edit form would do that silently.
  const [isPrimary, setIsPrimary] = useState(!hasPrimary);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  async function submit() {
    if (!name.trim()) { setProblem(S.needName); return; }
    setBusy(true);
    setProblem(null);
    const body = {
      name: name.trim(),
      governorate: governorate.trim() || null,
      address: address.trim() || null,
      geoLat: null,
      geoLng: null,
    };
    try {
      if (location) await networkApi.updateLocation(providerId, location.locationId, body);
      else await networkApi.createLocation(providerId, { ...body, isPrimary }, key);
      onSaved();
    } catch (e) {
      rotate();
      setProblem(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(location ? S.editLocation : S.newLocation)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={busy} onClick={() => void submit()}>{t(S.save)}</Button>
        </>
      }
    >
      <div className="pol-stack">
        {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
        <InputField
          label={t(S.name)}
          help={t(S.nameHint)}
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.governorate)}
          value={governorate}
          onChange={(e) => setGovernorate(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.address)}
          value={address}
          onChange={(e) => setAddress(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        {!location && (
          <CheckboxField
            label={t(S.isPrimaryField)}
            help={t(S.isPrimaryHint)}
            checked={isPrimary}
            onChange={(e) => setIsPrimary(e.currentTarget.checked)}
          />
        )}
      </div>
    </Modal>
  );
}

// ── Provider users ──────────────────────────────────────────────────────────────────────────────────────

function UsersPanel({ provider, mayWrite }: { provider: ProviderSummary; mayWrite: boolean }) {
  const t = useLoc();
  const fmt = useFormat();
  const [provisioning, setProvisioning] = useState(false);
  const [revoking, setRevoking] = useState<ProviderUserView | null>(null);

  const load = useCallback(() => networkApi.users(provider.id), [provider.id]);
  const [users, reload] = useLoad(load, [provider.id]);
  // The roles this CALLER may grant, from the server's own separation-of-duties rule. A hardcoded list here
  // was wrong on its first run: it offered `provider_user`, which is not a role this platform has.
  const detailLoad = useCallback(() => networkApi.provider(provider.id), [provider.id]);
  const [detail] = useLoad(detailLoad, [provider.id]);

  const columns: Column<ProviderUserView>[] = [
    { key: "subject", header: t(S.subject), cell: (u) => <span className="tnum">{u.subjectRef}</span>, sortable: true, sortValue: (u) => u.subjectRef },
    { key: "role", header: t(S.role), cell: (u) => u.role, sortable: true, sortValue: (u) => u.role },
    {
      key: "status", header: t(S.standing),
      cell: (u) => (
        <StatusChip
          kind={u.status === "Active" ? "ok" : "bad"}
          label={t(u.status === "Active" ? S.active : S.revoked)}
        />
      ),
      sortable: true, sortValue: (u) => u.status,
    },
    { key: "since", header: t(S.since), cell: (u) => <span className="tnum">{fmt.date(u.createdAt)}</span> },
    {
      key: "actions", header: "",
      cell: (u) => (mayWrite && u.status === "Active" ? (
        <div className="rst-actions">
          <Button variant="ghost" size="sm" aria-label={t(S.revoke)} title={t(S.revoke)} onClick={() => setRevoking(u)}>
            <Icon name="cross" />
          </Button>
        </div>
      ) : null),
    },
  ];

  return (
    <Card as="section">
      <div className="pol-stack">
        <div className="screen-toolbar">
          <div className="pay-head">
            <h3 style={{ margin: 0 }}>{t(S.users)}</h3>
            <p className="pol-muted" style={{ margin: 0 }}>{t(S.usersHint)}</p>
          </div>
          {mayWrite && (
            <Button variant="secondary" leadingIcon={<Icon name="plus" />} onClick={() => setProvisioning(true)}>
              {t(S.addUser)}
            </Button>
          )}
        </div>
        {/* One provider's staff accounts — bounded, and the revoked ones stay so an action taken last month
            is still attributable to somebody. */}
        <DataTable
          columns={columns}
          rows={users ?? []}
          rowKey={(u) => u.userId}
          caption={t(S.users)}
          emptyLabel={t(S.noUsers)}
        />
      </div>

      {provisioning && (
        <ProvisionUserForm
          providerId={provider.id}
          roles={detail?.provisionableRoles ?? []}
          onClose={() => setProvisioning(false)}
          onSaved={() => { setProvisioning(false); reload(); }}
        />
      )}

      {revoking && (
        <ReasonDialog
          title={fillLocalized(S.revokeTitle, revoking.subjectRef)}
          body={S.revokeBody}
          confirmLabel={S.revoke}
          onConfirm={async (reason, key) => { await networkApi.revokeUser(provider.id, revoking.userId, reason, key); }}
          onClose={() => setRevoking(null)}
          onDone={() => { setRevoking(null); reload(); }}
        />
      )}
    </Card>
  );
}

function ProvisionUserForm({
  providerId, roles, onClose, onSaved,
}: {
  providerId: string;
  roles: readonly string[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [subjectRef, setSubjectRef] = useState("");
  const [role, setRole] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(S.addUser)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button
            variant="primary"
            leadingIcon={<Icon name="check2" />}
            loading={busy}
            disabled={!subjectRef.trim() || !role}
            onClick={async () => {
              setBusy(true);
              setProblem(null);
              try {
                await networkApi.provisionUser(providerId, { subjectRef: subjectRef.trim(), role: role! }, key);
                onSaved();
              } catch (e) {
                rotate();
                setProblem(writeErrorMessage(e).message);
              } finally {
                setBusy(false);
              }
            }}
          >
            {t(S.save)}
          </Button>
        </>
      }
    >
      <div className="pol-stack">
        {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
        <InputField
          label={t(S.subjectField)}
          help={t(S.subjectHint)}
          value={subjectRef}
          onChange={(e) => setSubjectRef(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <ComboboxField
          label={t(S.roleField)}
          options={roles.map((r) => ({ value: r, label: r }))}
          value={role}
          onChange={setRole}
          required
        />
      </div>
    </Modal>
  );
}
