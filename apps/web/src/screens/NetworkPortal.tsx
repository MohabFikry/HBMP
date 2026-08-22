import { useCallback, useMemo, useState } from "react";
import {
  Button, Card, ComboboxField, DataTable, DataTableView, Icon, InlineAlert, InputField, KpiCard, KpiList,
  Modal, StatusChip, TextareaField, useTableQuery,
} from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type {
  CredentialWrite, Localized, NetworkMetrics, ProviderCredentialView, ProviderDetail, ProviderSummary,
  ProviderWrite,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerTheNetwork, mayReadTheNetworkRollup } from "../authz/permissions";
import { writeErrorMessage } from "../api/writeError";
import { AsyncSection, PageHeader, fillLocalized, useLoc } from "./_shared";
import { RecordActions, ReasonDialog } from "./AdminRecordControls";
import { useIdempotencyKey } from "./PolicyPanels";
import {
  Fact, NetworkHistoryModal, PROVIDER_TYPES, ReadinessChecklist, TYPE_LABEL, networkApi, useDate, useLoad,
} from "./NetworkAdminShared";

/**
 * Phase 19.9 — the provider network, administered (design 58).
 *
 * ============================================================================================================
 * WHAT THE PORTAL WAS
 * ============================================================================================================
 * Five sections and one write between them. The Directory listed providers and could not open one. Contracts
 * and Locations were read-only tables behind an unsearchable picker of the entire network. Onboarding was a
 * three-field form whose provider TYPE was a free-text box — type "hospital" in lower case and the create
 * failed with "unknown provider_type" — and which ended at Draft, with no way to reach any of the states
 * after it. Activate, suspend and terminate had endpoints and no buttons; a provider went live because
 * somebody ran curl.
 *
 * ============================================================================================================
 * THE DIRECTORY IS THE HUB
 * ============================================================================================================
 * One provider in full, chosen from the list: identity, standing and WHY it stands there, what is stopping it
 * going live, and how much hangs off it. Selecting a row is the only navigation, so there is no deep link
 * this screen promises and then breaks.
 *
 * ============================================================================================================
 * WHAT THE ROLE MAY DO IS ABSENT, NOT DISABLED
 * ============================================================================================================
 * Two roles share this portal (design 52 §5): Mersal's Network Team, and a contracted provider's OWN
 * administrator, who is ABAC- and RLS-bound to their own row. The second legitimately reads their record —
 * and must not edit the contract Mersal signed with them. `provider:admin` is the scope that draws that line
 * server-side; `mayAdministerTheNetwork` mirrors it here so the second role sees a record rather than four
 * buttons that answer 403.
 */

const S = {
  // ── directory ───────────────────────────────────────────────────────────────────────────────────────────
  dirTitle: { en: "Providers Directory", ar: "دليل مقدمي الخدمة" },
  dirSubtitle: {
    en: "Every provider in the network, what standing they are in, and how far through onboarding.",
    ar: "كل مقدمي الخدمة في الشبكة، وحالة كل منهم، وموقعه من مراحل الانضمام.",
  },
  dirEmpty: { en: "No providers in this network.", ar: "لا يوجد مقدمو خدمة في هذه الشبكة." },
  provider: { en: "Provider", ar: "مقدم الخدمة" },
  codeH: { en: "Code", ar: "الرمز" },
  typeH: { en: "Type", ar: "النوع" },
  status: { en: "Status", ar: "الحالة" },
  onboarding: { en: "Onboarding", ar: "الانضمام" },
  search: { en: "Search", ar: "بحث" },
  dirSearchHint: { en: "Provider name or code", ar: "اسم مقدم الخدمة أو رمزه" },
  noMatches: {
    en: "No providers match. Change the search or clear the filters.",
    ar: "لا يوجد مقدمو خدمة مطابقون. عدّل البحث أو أزل عوامل التصفية.",
  },
  selectProvider: {
    en: "Select a provider to see its record, what is outstanding, and the controls for it.",
    ar: "اختر مقدم خدمة لعرض سجله وما هو معلّق والتحكم فيه.",
  },
  // ── performance ─────────────────────────────────────────────────────────────────────────────────────────
  perfTitle: { en: "Performance", ar: "الأداء" },
  total: { en: "Providers", ar: "مقدمو الخدمة" },
  active: { en: "Active", ar: "نشط" },
  suspended: { en: "Suspended", ar: "موقوف" },
  terminated: { en: "Terminated", ar: "منتهٍ" },
  notYourNetwork: {
    en: "This is the network-wide view, which belongs to Mersal's Network Team. A provider's own administrator sees their own organisation — its directory entry, contracts and locations are in the sections above.",
    ar: "هذه نظرة على الشبكة بالكامل، وهي من اختصاص فريق الشبكة في مرسال. أما مسؤول مقدم الخدمة فيرى مؤسسته وحدها — بيانها في الدليل وعقودها ومواقعها في الأقسام أعلاه.",
  },
  // ── detail ──────────────────────────────────────────────────────────────────────────────────────────────
  identity: { en: "Identity", ar: "التعريف" },
  legalName: { en: "Legal name", ar: "الاسم القانوني" },
  commercialName: { en: "Trading name", ar: "الاسم التجاري" },
  commercialNameHint: {
    en: "The name on the building, when it differs from the name on the contract. Referrals and the directory show this one.",
    ar: "الاسم على اللافتة إذا اختلف عن الاسم في العقد. تعرضه الإحالات والدليل.",
  },
  code: { en: "Provider code", ar: "رمز مقدم الخدمة" },
  codeHint: {
    en: "Cited by every contract, claim and invoice. It is set once and cannot be changed.",
    ar: "يُستشهد به في كل عقد ومطالبة وفاتورة. يُحدَّد مرة واحدة ولا يمكن تغييره.",
  },
  type: { en: "Type", ar: "النوع" },
  taxId: { en: "Tax card number", ar: "رقم البطاقة الضريبية" },
  phone: { en: "Phone", ar: "الهاتف" },
  email: { en: "Email", ar: "البريد الإلكتروني" },
  notes: { en: "Notes", ar: "ملاحظات" },
  book: { en: "What hangs off this provider", ar: "ما يرتبط بمقدم الخدمة" },
  locationsCount: { en: "Locations", ar: "المواقع" },
  contractsCount: { en: "Contracts in effect", ar: "العقود السارية" },
  credentialsCount: { en: "Documents", ar: "المستندات" },
  usersCount: { en: "Accounts", ar: "الحسابات" },
  recordedBy: { en: "Created by", ar: "أنشأه" },
  changedBy: { en: "Last changed by", ar: "آخر تعديل بواسطة" },
  notRecorded: { en: "Not recorded", ar: "غير مسجّل" },
  statusReason: { en: "Why", ar: "السبب" },
  standingSince: { en: "Since", ar: "منذ" },
  // ── controls ────────────────────────────────────────────────────────────────────────────────────────────
  edit: { en: "Edit provider", ar: "تعديل مقدم الخدمة" },
  history: { en: "Change history", ar: "سجل التغييرات" },
  activate: { en: "Activate", ar: "تفعيل" },
  reactivate: { en: "Reactivate", ar: "إعادة التفعيل" },
  suspend: { en: "Suspend", ar: "إيقاف" },
  terminate: { en: "Terminate", ar: "إنهاء" },
  withdrawTermination: { en: "Withdraw the termination request", ar: "سحب طلب الإنهاء" },
  activateTitle: { en: "Activate {0}?", ar: "تفعيل {0}؟" },
  activateBody: {
    en: "The provider becomes routable: orders and referrals start reaching them, and claims settle at their contract prices.",
    ar: "يصبح مقدم الخدمة قابلًا للتوجيه: تبدأ الطلبات والإحالات في الوصول إليه، وتُسوّى المطالبات بأسعار عقده.",
  },
  suspendTitle: { en: "Suspend {0}?", ar: "إيقاف {0}؟" },
  suspendBody: {
    en: "Routing stops immediately and EVERY account belonging to this provider is revoked in the same step. Reversible: reactivating restores routing, but accounts are provisioned again individually.",
    ar: "يتوقف التوجيه فورًا وتُلغى جميع حسابات مقدم الخدمة في الخطوة نفسها. قابل للعكس: إعادة التفعيل تستعيد التوجيه، أما الحسابات فتُنشأ مجددًا فرديًا.",
  },
  terminateTitle: { en: "Request termination of {0}?", ar: "طلب إنهاء التعاقد مع {0}؟" },
  terminateBody: {
    en: "This opens a request and changes nothing yet. A DIFFERENT user must repeat it to approve — terminating drops the provider out of the network and revokes every account they hold.",
    ar: "يفتح هذا طلبًا ولا يغيّر شيئًا بعد. يجب أن يكرره مستخدم آخر للموافقة — فالإنهاء يُخرج مقدم الخدمة من الشبكة ويلغي كل حساباته.",
  },
  approveTerminationTitle: { en: "Approve the termination of {0}?", ar: "الموافقة على إنهاء التعاقد مع {0}؟" },
  approveTerminationBody: {
    en: "This is the second approval. It terminates the provider, drops them out of the routable network, and revokes every account they hold. It is not reversible.",
    ar: "هذه هي الموافقة الثانية. تُنهي التعاقد وتُخرج مقدم الخدمة من الشبكة القابلة للتوجيه وتلغي كل حساباته. وهي غير قابلة للعكس.",
  },
  withdrawTitle: { en: "Withdraw the termination request?", ar: "سحب طلب الإنهاء؟" },
  withdrawBody: {
    en: "The request is closed and the provider is untouched. Anyone with network administration may withdraw one — dual control exists to stop a single person terminating a provider, not to stop a colleague closing a request that should not have been opened.",
    ar: "يُغلَق الطلب ولا يتأثر مقدم الخدمة. يمكن لأي مسؤول شبكة سحبه — فالرقابة الثنائية موجودة لمنع شخص واحد من إنهاء التعاقد، لا لمنع زميل من إغلاق طلب ما كان ينبغي فتحه.",
  },
  pendingTermination: {
    en: "A termination of this provider was requested by {0} and is waiting for a second approver. Nothing has changed yet.",
    ar: "طُلب إنهاء التعاقد مع مقدم الخدمة من {0} وينتظر موافقًا ثانيًا. لم يتغير شيء بعد.",
  },
  cannotActivateYet: {
    en: "Activation is blocked until the checklist above is complete.",
    ar: "التفعيل متوقف حتى تكتمل القائمة أعلاه.",
  },
  // ── the form ────────────────────────────────────────────────────────────────────────────────────────────
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  needName: { en: "A legal name is required.", ar: "الاسم القانوني مطلوب." },
  needType: { en: "A provider type is required.", ar: "نوع مقدم الخدمة مطلوب." },
  // ── onboarding ──────────────────────────────────────────────────────────────────────────────────────────
  onboardTitle: { en: "Onboarding", ar: "الانضمام" },
  onboardSubtitle: {
    en: "Bringing a provider from first contact to routable: their record, a primary location, their documents, a contract in force.",
    ar: "نقل مقدم الخدمة من أول تواصل إلى قابلية التوجيه: سجله، وموقع رئيسي، ومستنداته، وعقد ساري.",
  },
  onboardNew: { en: "Onboard a provider", ar: "ضم مقدم خدمة" },
  created: { en: "Provider created as a draft. Its onboarding checklist is below.", ar: "أُنشئ مقدم الخدمة كمسودة. قائمة انضمامه أدناه." },
  inProgress: { en: "Not yet live", ar: "لم يُفعَّل بعد" },
  inProgressHint: {
    en: "Providers that are not routable yet. Choose one to see what it is waiting on.",
    ar: "مقدمو الخدمة غير القابلين للتوجيه بعد. اختر واحدًا لمعرفة ما ينتظره.",
  },
  allLive: {
    en: "Every provider in the network is live. New ones appear here as soon as they are created.",
    ar: "كل مقدمي الخدمة في الشبكة مفعّلون. يظهر الجدد هنا فور إنشائهم.",
  },
  onboardingNotYours: {
    en: "Onboarding a provider is Mersal's Network Team's to do. Your own organisation's record, contracts and locations are in the sections above.",
    ar: "ضم مقدمي الخدمة من اختصاص إدارة الشبكة في مرسال. أما سجل مؤسستك وعقودها ومواقعها فموجودة في الأقسام أعلاه.",
  },
  // ── credentials ─────────────────────────────────────────────────────────────────────────────────────────
  credentials: { en: "Documents & credentials", ar: "المستندات والاعتمادات" },
  credentialsHint: {
    en: "Licences, tax cards and accreditations. Activation is gated on every MANDATORY one being attached and unexpired.",
    ar: "التراخيص والبطاقات الضريبية والاعتمادات. يتوقف التفعيل على إرفاق كل مستند إلزامي وعدم انتهائه.",
  },
  noCredentials: { en: "No documents recorded.", ar: "لا توجد مستندات مسجلة." },
  credentialType: { en: "Document", ar: "المستند" },
  validFrom: { en: "Valid from", ar: "صالح من" },
  validTo: { en: "Expires", ar: "ينتهي" },
  mandatory: { en: "Mandatory", ar: "إلزامي" },
  optional: { en: "Optional", ar: "اختياري" },
  expiresIn: { en: "in {0} days", ar: "خلال {0} يومًا" },
  expired: { en: "Expired", ar: "منتهٍ" },
  addCredential: { en: "Record a document", ar: "تسجيل مستند" },
  editCredential: { en: "Edit document", ar: "تعديل المستند" },
  withdrawCredential: { en: "Withdraw", ar: "سحب" },
  withdrawCredTitle: { en: "Withdraw {0}?", ar: "سحب {0}؟" },
  withdrawCredBody: {
    en: "The document stops counting towards this provider's credentialing. The record is kept with your reason.",
    ar: "يتوقف احتساب المستند ضمن اعتماد مقدم الخدمة. ويُحفَظ السجل مع سببك.",
  },
  belowBarNow: {
    en: "This provider is Active and no longer meets its own activation bar. Their status has not been changed — that is a separate decision — but nothing else will tell you.",
    ar: "مقدم الخدمة نشط ولم يعد مستوفيًا لشروط تفعيله. لم تتغير حالته — فذلك قرار منفصل — ولن يخبرك بذلك شيء آخر.",
  },
  documentRef: { en: "Document reference", ar: "مرجع المستند" },
  documentRefHint: {
    en: "The id of the scanned document in document-service. A credential cannot be marked valid without one.",
    ar: "معرّف المستند الممسوح ضوئيًا في خدمة المستندات. لا يمكن اعتماد المستند بدونه.",
  },
  credStatus: { en: "Standing", ar: "الحالة" },
  mandatoryField: { en: "Activation depends on this document", ar: "يتوقف التفعيل على هذا المستند" },
  mandatoryHint: {
    en: "A mandatory document must be attached and unexpired before the provider can go live.",
    ar: "يجب إرفاق المستند الإلزامي وعدم انتهائه قبل تفعيل مقدم الخدمة.",
  },
} satisfies Record<string, Localized>;

const CREDENTIAL_STATUSES = ["Pending", "Valid", "Expired", "Rejected"] as const;

const HISTORY_LABELS: Record<string, Localized> = {
  legal_name: S.legalName,
  commercial_name: S.commercialName,
  provider_type: S.type,
  status: S.status,
  onboarding_state: S.onboarding,
  tax_id: S.taxId,
  phone: S.phone,
  email: S.email,
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// Directory
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

/** Providers directory — the tenant's whole network, and one provider in full. */
export function NetworkDirectory() {
  const api = useApi();
  const t = useLoc();
  const { session } = useAuth();
  const mayWrite = mayAdministerTheNetwork(session?.issuerRoles);

  const state = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  const [selected, setSelected] = useState<string | null>(null);

  const cols = directoryColumns(t);

  /*
    This was the WHOLE network in one response rendered as one unbroken list: no search, no filter, no pager.
    Finding a provider meant scrolling past every other one.

    Both filter groups are derived from the rows rather than declared, for the reason the clinician panel
    gives about branches: the vocabulary is the network's, not the client's. A hardcoded type list would show
    a chip for Imaging in a network with no imaging centre, and miss whatever is added next.
  */
  const rows = useMemo(() => state.data ?? [], [state.data]);
  const filters: TableFilterSpec<ProviderSummary>[] = useMemo(() => {
    const groups: TableFilterSpec<ProviderSummary>[] = [];
    const types = [...new Set(rows.map((r) => r.providerType))].sort((a, b) => a.localeCompare(b));
    if (types.length > 1) {
      groups.push({
        key: "type",
        label: t(S.typeH),
        options: types.map((x) => ({ value: x, label: x })),
        match: (r, value) => r.providerType === value,
      });
    }
    const states = [...new Set(rows.map((r) => r.onboardingState))].sort((a, b) => a.localeCompare(b));
    if (states.length > 1) {
      groups.push({
        key: "onboarding",
        label: t(S.onboarding),
        options: states.map((x) => ({ value: x, label: x })),
        match: (r, value) => r.onboardingState === value,
      });
    }
    return groups;
  }, [rows, t]);

  const query = useTableQuery<ProviderSummary>({
    rows,
    columns: cols,
    // The provider code is what a claim or a contract cites, so it has to be searchable even though the
    // name is what an operator would think of first.
    searchText: (r) => [r.legalName, r.code, r.providerType].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.dirSearchHint),
    filters,
    pageSize: 25,
    initialSortKey: "provider",
    persistKey: "network-directory",
  });

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.dirTitle)} />
      <p className="pol-muted">{t(S.dirSubtitle)}</p>
      <Card as="section">
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.dirEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.dirTitle)}
              emptyLabel={t(S.dirEmpty)}
              noMatchesLabel={t(S.noMatches)}
              interactive
              selectedKey={selected}
              onSelect={(r) => setSelected(r.id)}
            />
          )}
        </AsyncSection>
      </Card>

      {!selected && rows.length > 0 && <InlineAlert tone="info">{t(S.selectProvider)}</InlineAlert>}
      {selected && (
        <ProviderRecord providerId={selected} mayWrite={mayWrite} onChanged={() => state.reload()} />
      )}
    </div>
  );
}

function directoryColumns(t: (l: Localized) => string): Column<ProviderSummary>[] {
  return [
    { key: "provider", header: t(S.provider), cell: (r) => r.legalName, sortable: true, sortValue: (r) => r.legalName },
    { key: "code", header: t(S.codeH), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    { key: "type", header: t(S.typeH), cell: (r) => r.providerType, sortable: true, sortValue: (r) => r.providerType },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "onboarding", header: t(S.onboarding), cell: (r) => <StatusChip kind="neu" label={r.onboardingState} />, sortable: true, sortValue: (r) => r.onboardingState },
  ];
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// One provider in full
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

type StatusMove = "activate" | "suspend" | "terminate" | "withdraw";

export function ProviderRecord({
  providerId, mayWrite, onChanged,
}: {
  providerId: string;
  mayWrite: boolean;
  onChanged?: () => void;
}) {
  const t = useLoc();
  const date = useDate();
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  const [editing, setEditing] = useState(false);
  const [move, setMove] = useState<StatusMove | null>(null);
  const [history, setHistory] = useState(false);

  const load = useCallback(() => networkApi.provider(providerId), [providerId]);
  const [detail, reload] = useLoad(load, [providerId]);

  const after = useCallback(() => {
    reload();
    onChanged?.();
  }, [reload, onChanged]);

  if (!detail) return null;
  const live = detail.status === "Active";
  const terminated = detail.status === "Terminated";

  return (
    <div className="pol-detail">
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <Card as="section">
        <div className="pol-stack">
          <div className="screen-toolbar">
            <div className="pay-head">
              <h2 style={{ margin: 0 }}>{detail.commercialName || detail.legalName}</h2>
              <div className="pay-chips">
                <span className="tnum pol-muted">{detail.providerCode}</span>
                <span className="pol-muted">{t(TYPE_LABEL[detail.providerType] ?? { en: detail.providerType, ar: detail.providerType })}</span>
                <StatusChip kind={live ? "ok" : terminated ? "bad" : "warn"} label={detail.status} />
                <StatusChip kind="neu" label={detail.onboardingState} />
              </div>
            </div>
            <RecordActions
              onHistory={() => setHistory(true)}
              onEdit={mayWrite && !terminated ? () => setEditing(true) : undefined}
              editLabel={S.edit}
              status={
                !mayWrite || terminated
                  ? undefined
                  : live
                    ? { label: S.suspend, icon: "cross", onClick: () => setMove("suspend") }
                    : { label: detail.onboardingState === "Suspended" ? S.reactivate : S.activate, icon: "check2", onClick: () => setMove("activate") }
              }
            >
              {mayWrite && !terminated && (
                <Button
                  variant="ghost"
                  aria-label={t(detail.pendingTermination ? S.withdrawTermination : S.terminate)}
                  title={t(detail.pendingTermination ? S.withdrawTermination : S.terminate)}
                  onClick={() => setMove(detail.pendingTermination ? "withdraw" : "terminate")}
                >
                  <Icon name={detail.pendingTermination ? "undo" : "bin"} />
                </Button>
              )}
            </RecordActions>
          </div>

          {/* An open dual-controlled termination is the single most important thing about this record while
              it is open, and nothing said so: the provider looked ordinary and the second approver had no
              way to discover there was anything to approve. */}
          {detail.pendingTermination && (
            <InlineAlert tone="warn">
              {t(fillLocalized(S.pendingTermination, detail.pendingTermination.requestedBy))}
              {" "}
              {detail.pendingTermination.reason}
            </InlineAlert>
          )}

          {/* Why the provider stands where it does — the operational record, distinct from the hash-chained
              audit trail, which is Compliance's and not readable by the team administering the network. */}
          {detail.statusReason && (
            <dl className="pol-identity-list">
              <Fact label={t(S.statusReason)} value={detail.statusReason} />
              <Fact label={t(S.standingSince)} value={date(detail.statusChangedAt)} mono />
              <Fact label={t(S.changedBy)} value={detail.statusActorName ?? t(S.notRecorded)} />
            </dl>
          )}

          <dl className="pol-identity-list">
            <Fact label={t(S.legalName)} value={detail.legalName} />
            <Fact label={t(S.code)} value={detail.providerCode} mono />
            <Fact label={t(S.taxId)} value={detail.taxId || t(S.notRecorded)} mono />
            <Fact label={t(S.phone)} value={detail.phone || t(S.notRecorded)} mono />
            <Fact label={t(S.email)} value={detail.email || t(S.notRecorded)} />
            <Fact label={t(S.recordedBy)} value={detail.createdByName ?? t(S.notRecorded)} />
            <Fact label={t(S.changedBy)} value={detail.updatedByName ?? t(S.notRecorded)} />
          </dl>
          {detail.notes && <p className="pol-muted">{detail.notes}</p>}
        </div>
      </Card>

      <Card as="section">
        <ReadinessChecklist readiness={detail.readiness} />
      </Card>

      <Card as="section">
        <div className="pol-stack">
          <h3 style={{ margin: 0 }}>{t(S.book)}</h3>
          <KpiList
            items={[
              { label: t(S.locationsCount), value: String(detail.book.locations) },
              { label: t(S.contractsCount), value: `${detail.book.activeContracts} / ${detail.book.contracts}` },
              { label: t(S.credentialsCount), value: String(detail.book.credentials) },
              { label: t(S.usersCount), value: String(detail.book.activeUsers) },
            ]}
          />
        </div>
      </Card>

      <Card as="section">
        <CredentialsPanel
          providerId={providerId}
          mayWrite={mayWrite}
          providerLive={live}
          onChanged={after}
          onError={setError}
        />
      </Card>

      {editing && (
        <ProviderForm
          detail={detail}
          onClose={() => setEditing(false)}
          onSaved={() => { setEditing(false); setAnnounce(t(S.edit)); after(); }}
        />
      )}

      {move && (
        <StatusMoveDialog
          detail={detail}
          move={move}
          onClose={() => setMove(null)}
          onDone={() => { setMove(null); after(); }}
        />
      )}

      {history && (
        <NetworkHistoryModal
          title={S.history}
          labels={HISTORY_LABELS}
          load={() => networkApi.providerHistory(providerId)}
          onClose={() => setHistory(false)}
        />
      )}
    </div>
  );
}

// ── Status moves ────────────────────────────────────────────────────────────────────────────────────────

function StatusMoveDialog({
  detail, move, onClose, onDone,
}: {
  detail: ProviderDetail;
  move: StatusMove;
  onClose: () => void;
  onDone: () => void;
}) {
  const name = detail.commercialName || detail.legalName;

  // Terminating splits in two, and the wording has to as well: the FIRST call opens a request and changes
  // nothing, the second — from a different token — is the one that ends the relationship. Presenting both
  // as "Terminate?" is how a second approver clicks through believing they are only asking.
  const approving = move === "terminate" && Boolean(detail.pendingTermination);

  const copy: Record<StatusMove, { title: Localized; body: Localized; confirm: Localized }> = {
    activate: {
      title: fillLocalized(S.activateTitle, name),
      body: S.activateBody,
      confirm: detail.onboardingState === "Suspended" ? S.reactivate : S.activate,
    },
    suspend: { title: fillLocalized(S.suspendTitle, name), body: S.suspendBody, confirm: S.suspend },
    terminate: approving
      ? { title: fillLocalized(S.approveTerminationTitle, name), body: S.approveTerminationBody, confirm: S.terminate }
      : { title: fillLocalized(S.terminateTitle, name), body: S.terminateBody, confirm: S.terminate },
    withdraw: { title: S.withdrawTitle, body: S.withdrawBody, confirm: S.withdrawTermination },
  };

  const blocked = move === "activate" && !detail.readiness.canActivate;

  return (
    <ReasonDialog
      title={copy[move].title}
      body={copy[move].body}
      confirmLabel={copy[move].confirm}
      onConfirm={async (reason, key) => {
        if (move === "activate") await networkApi.activateProvider(detail.providerId, reason, key);
        else if (move === "suspend") await networkApi.suspendProvider(detail.providerId, reason, key);
        else if (move === "terminate") await networkApi.terminateProvider(detail.providerId, reason, key);
        else await networkApi.withdrawTermination(detail.providerId, reason, key);
      }}
      onClose={onClose}
      onDone={onDone}
    >
      {/* Said before the attempt. The server still refuses — this is not a substitute for the guard, it is
          the operator not having to discover the guard by tripping it. */}
      {blocked && <ActivationBlocked reason={detail.readiness.blockingReason ?? null} />}
    </ReasonDialog>
  );
}

function ActivationBlocked({ reason }: { reason: string | null }) {
  const t = useLoc();
  return (
    <InlineAlert tone="warn">
      {t(S.cannotActivateYet)}
      {reason ? ` ${reason}` : ""}
    </InlineAlert>
  );
}

// ── The provider form ───────────────────────────────────────────────────────────────────────────────────

function ProviderForm({
  detail, onClose, onSaved,
}: {
  detail: ProviderDetail;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const [legalName, setLegalName] = useState(detail.legalName);
  const [commercialName, setCommercialName] = useState(detail.commercialName ?? "");
  const [type, setType] = useState<string | null>(detail.providerType);
  const [taxId, setTaxId] = useState(detail.taxId ?? "");
  const [phone, setPhone] = useState(detail.phone ?? "");
  const [email, setEmail] = useState(detail.email ?? "");
  const [notes, setNotes] = useState(detail.notes ?? "");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  async function submit() {
    if (!legalName.trim()) { setProblem(S.needName); return; }
    if (!type) { setProblem(S.needType); return; }
    setBusy(true);
    setProblem(null);
    const body: ProviderWrite = {
      // Sent unchanged and CHECKED by the server. The field is read-only here rather than absent so the
      // operator can see what it is — and so a corrected code is refused out loud rather than discarded.
      providerCode: detail.providerCode,
      legalName: legalName.trim(),
      providerType: type,
      commercialName: commercialName.trim() || null,
      taxId: taxId.trim() || null,
      phone: phone.trim() || null,
      email: email.trim() || null,
      notes: notes.trim() || null,
    };
    try {
      await networkApi.updateProvider(detail.providerId, body);
      onSaved();
    } catch (e) {
      setProblem(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(S.edit)}
      closeLabel={t(S.cancel)}
      wide
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
          label={t(S.code)}
          help={t(S.codeHint)}
          value={detail.providerCode}
          readOnly
          className="tnum"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.legalName)}
          value={legalName}
          onChange={(e) => setLegalName(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.commercialName)}
          help={t(S.commercialNameHint)}
          value={commercialName}
          onChange={(e) => setCommercialName(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <ComboboxField
          label={t(S.type)}
          options={PROVIDER_TYPES.map((v) => ({ value: v, label: t(TYPE_LABEL[v] ?? { en: v, ar: v }) }))}
          value={type}
          onChange={setType}
          required
        />
        <InputField
          label={t(S.taxId)}
          value={taxId}
          onChange={(e) => setTaxId(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.phone)}
          type="tel"
          value={phone}
          onChange={(e) => setPhone(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.email)}
          type="email"
          value={email}
          onChange={(e) => setEmail(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <TextareaField
          label={t(S.notes)}
          rows={3}
          value={notes}
          onChange={(e) => setNotes(e.currentTarget.value)}
        />
      </div>
    </Modal>
  );
}

// ── Credentials ─────────────────────────────────────────────────────────────────────────────────────────

function CredentialsPanel({
  providerId, mayWrite, providerLive, onChanged, onError,
}: {
  providerId: string;
  mayWrite: boolean;
  providerLive: boolean;
  onChanged: () => void;
  onError: (l: Localized | null) => void;
}) {
  const t = useLoc();
  const date = useDate();
  const [form, setForm] = useState<{ mode: "create" } | { mode: "edit"; credential: ProviderCredentialView } | null>(null);
  const [withdrawing, setWithdrawing] = useState<ProviderCredentialView | null>(null);

  const load = useCallback(() => networkApi.credentials(providerId), [providerId]);
  const [credentials, reload] = useLoad(load, [providerId]);

  const columns: Column<ProviderCredentialView>[] = [
    { key: "type", header: t(S.credentialType), cell: (c) => c.credentialType, sortable: true, sortValue: (c) => c.credentialType },
    {
      key: "mandatory", header: t(S.mandatory),
      cell: (c) => <StatusChip kind={c.isMandatory ? "info" : "neu"} label={t(c.isMandatory ? S.mandatory : S.optional)} />,
      sortable: true, sortValue: (c) => (c.isMandatory ? "0" : "1"),
    },
    {
      key: "status", header: t(S.credStatus),
      cell: (c) => (
        <div className="pay-chips">
          <StatusChip kind={c.isDeleted ? "neu" : c.validToday ? "ok" : "warn"} label={c.status} />
          {/* The number, not a colour: "expires in 12 days" is actionable and amber is not. */}
          {!c.isDeleted && typeof c.daysUntilExpiry === "number" && (
            <span className="pol-muted">
              {c.daysUntilExpiry < 0 ? t(S.expired) : t(fillLocalized(S.expiresIn, String(c.daysUntilExpiry)))}
            </span>
          )}
        </div>
      ),
    },
    { key: "from", header: t(S.validFrom), cell: (c) => <span className="tnum">{date(c.validFrom)}</span> },
    { key: "to", header: t(S.validTo), cell: (c) => <span className="tnum">{date(c.validTo)}</span> },
    {
      key: "actions", header: "",
      cell: (c) => (mayWrite && !c.isDeleted ? (
        <div className="rst-actions">
          <Button variant="ghost" size="sm" aria-label={t(S.editCredential)} title={t(S.editCredential)} onClick={() => setForm({ mode: "edit", credential: c })}>
            <Icon name="pen" />
          </Button>
          <Button variant="ghost" size="sm" aria-label={t(S.withdrawCredential)} title={t(S.withdrawCredential)} onClick={() => setWithdrawing(c)}>
            <Icon name="cross" />
          </Button>
        </div>
      ) : null),
    },
  ];

  return (
    <div className="pol-stack">
      <div className="screen-toolbar">
        <div className="pay-head">
          <h3 style={{ margin: 0 }}>{t(S.credentials)}</h3>
          <p className="pol-muted" style={{ margin: 0 }}>{t(S.credentialsHint)}</p>
        </div>
        {mayWrite && (
          <Button variant="secondary" leadingIcon={<Icon name="plus" />} onClick={() => setForm({ mode: "create" })}>
            {t(S.addCredential)}
          </Button>
        )}
      </div>

      {/* One provider's documents — a handful, and the withdrawn ones stay so a lapsed licence is still
          explicable. */}
      <DataTable
        columns={columns}
        rows={credentials ?? []}
        rowKey={(c) => c.credentialId}
        caption={t(S.credentials)}
        emptyLabel={t(S.noCredentials)}
      />

      {form && (
        <CredentialForm
          providerId={providerId}
          credential={form.mode === "edit" ? form.credential : null}
          onClose={() => setForm(null)}
          onSaved={() => { setForm(null); reload(); onChanged(); }}
        />
      )}

      {withdrawing && (
        <WithdrawCredentialDialog
          providerId={providerId}
          credential={withdrawing}
          providerLive={providerLive}
          onClose={() => setWithdrawing(null)}
          onDone={(msg) => { setWithdrawing(null); reload(); onChanged(); onError(msg); }}
        />
      )}
    </div>
  );
}

function WithdrawCredentialDialog({
  providerId, credential, providerLive, onClose, onDone,
}: {
  providerId: string;
  credential: ProviderCredentialView;
  providerLive: boolean;
  onClose: () => void;
  onDone: (message: Localized | null) => void;
}) {
  const [result, setResult] = useState<Localized | null>(null);
  return (
    <ReasonDialog
      title={fillLocalized(S.withdrawCredTitle, credential.credentialType)}
      body={S.withdrawCredBody}
      confirmLabel={S.withdrawCredential}
      onConfirm={async (reason, key) => {
        const r = await networkApi.withdrawCredential(providerId, credential.credentialId, reason, key);
        // Recomputed by the SERVER after the write, against the same rules the activation guard uses.
        setResult(r.providerNoLongerMeetsActivationBar ? S.belowBarNow : null);
      }}
      onClose={onClose}
      onDone={() => onDone(result)}
    >
      {providerLive && credential.isMandatory && <MandatoryWarning />}
    </ReasonDialog>
  );
}

function MandatoryWarning() {
  const t = useLoc();
  return <InlineAlert tone="warn">{t(S.belowBarNow)}</InlineAlert>;
}

function CredentialForm({
  providerId, credential, onClose, onSaved,
}: {
  providerId: string;
  credential: ProviderCredentialView | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [type, setType] = useState(credential?.credentialType ?? "");
  const [status, setStatus] = useState<string | null>(credential?.status ?? "Pending");
  const [validFrom, setValidFrom] = useState(credential?.validFrom ?? "");
  const [validTo, setValidTo] = useState(credential?.validTo ?? "");
  const [documentId, setDocumentId] = useState(credential?.documentId ?? "");
  const [mandatory, setMandatory] = useState(credential?.isMandatory ?? true);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  async function submit() {
    setBusy(true);
    setProblem(null);
    const body: CredentialWrite = {
      credentialType: type.trim(),
      status: status ?? "Pending",
      validFrom: validFrom || null,
      validTo: validTo || null,
      documentId: documentId.trim() || null,
      isMandatory: mandatory,
    };
    try {
      if (credential) await networkApi.updateCredential(providerId, credential.credentialId, body);
      else {
        await networkApi.addCredential(providerId, body, key);
      }
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
      title={t(credential ? S.editCredential : S.addCredential)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={busy} disabled={!type.trim()} onClick={() => void submit()}>{t(S.save)}</Button>
        </>
      }
    >
      <div className="pol-stack">
        {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
        <InputField
          label={t(S.credentialType)}
          value={type}
          onChange={(e) => setType(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        {credential && (
          <ComboboxField
            label={t(S.credStatus)}
            options={CREDENTIAL_STATUSES.map((v) => ({ value: v, label: v }))}
            value={status}
            onChange={setStatus}
            required
          />
        )}
        <InputField
          label={t(S.validFrom)}
          type="date"
          value={validFrom}
          onChange={(e) => setValidFrom(e.currentTarget.value)}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.validTo)}
          type="date"
          value={validTo}
          onChange={(e) => setValidTo(e.currentTarget.value)}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.documentRef)}
          help={t(S.documentRefHint)}
          value={documentId}
          onChange={(e) => setDocumentId(e.currentTarget.value)}
          autoComplete="off"
          className="tnum"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <label className="mrs-checkrow">
          <input
            type="checkbox"
            className="mrs-checkbox"
            checked={mandatory}
            onChange={(e) => setMandatory(e.currentTarget.checked)}
          />
          <span>{t(S.mandatoryField)}</span>
        </label>
        <p className="pol-muted" style={{ margin: 0 }}>{t(S.mandatoryHint)}</p>
      </div>
    </Modal>
  );
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// Performance
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

/**
 * Performance — the network roll-up, from provider-service.
 *
 * ============================================================================================================
 * 33.7 — THE FOUR NUMBERS THAT WERE COUNTED IN THE BROWSER
 * ============================================================================================================
 * This screen used to fetch the provider DIRECTORY and count it:
 *
 *   const by = (label: string) => rows.filter((r) => r.status.label.en === label).length;
 *
 * `status` is the `{kind, label}` chip this client assembles for RENDERING. So the roll-up was a tally of a
 * piece of English prose, over whatever the directory projection happened to return, and it would have gone
 * to four zeroes — silently, plausibly — the first time a status was relabelled or a new one was added that
 * `providerStatusChip` did not recognise.
 *
 * `GET /api/v1/metrics` has returned exactly `{total, active, suspended, terminated}` since phase 2b,
 * computed from the `ProviderStatus` enum over the tenant, excluding soft-deleted rows. It was never routed
 * at the gateway — and the route-coverage guard whose whole job is to catch an unrouted resource had
 * "metrics" in its ignore list, meant for the Prometheus scrape. Both are fixed; this asks the service.
 *
 * The authorization is the part that makes this more than tidiness. The endpoint answers a provider-scoped
 * caller with 403: a provider must not learn the shape of the network it competes in. A count assembled from
 * a list the caller can already read enforces none of that.
 */
export function NetworkPerformance() {
  const t = useLoc();
  const { session } = useAuth();
  /*
    TWO ROLES SHARE THIS PORTAL and only one of them may read this.

    `ROLE_MAP` maps both the issuer's `network_team` (Mersal's Network Team — tenant-wide, T2) and its
    `provider_admin` (one provider's own administrator — T4, bound to that provider by ABAC and RLS) onto the
    single portal role `provider_admin`. provider-service answers this endpoint with 403 for the second, and
    it is right to: a provider must not learn the shape of the network it competes in.

    So the section says whose view it is rather than fetching and rendering the refusal as an error. Hiding
    it outright would be better still — the org-admin portal drops the tenant registry for exactly this
    reason (28.10) — and it cannot be done from a permission, because both roles carry the same portal
    permissions by construction. The portal split that fixes it properly is design 52 §5.
  */
  if (!mayReadTheNetworkRollup(session?.issuerRoles)) {
    return (
      <>
        <PageHeader title={t(S.perfTitle)} />
        <Card as="section" padded>
          <InlineAlert tone="info">{t(S.notYourNetwork)}</InlineAlert>
        </Card>
      </>
    );
  }
  return <NetworkRollup />;
}

function NetworkRollup() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<NetworkMetrics>(() => api.networkMetrics(), []);
  return (
    <>
      <PageHeader title={t(S.perfTitle)} />
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.dirEmpty}>
        {(m) => (
          <div className="kpi-row">
            <KpiCard label={t(S.total)} value={String(m.total)} />
            <KpiCard label={t(S.active)} value={String(m.active)} />
            <KpiCard label={t(S.suspended)} value={String(m.suspended)} />
            <KpiCard label={t(S.terminated)} value={String(m.terminated)} />
          </div>
        )}
      </AsyncSection>
    </>
  );
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// Onboarding
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

/**
 * Onboarding — from first contact to routable.
 *
 * <p>This was a three-field form that created a Draft and stopped. The provider TYPE was a free-text input
 * validated against a list the operator could not see, so "hospital" in lower case failed with "unknown
 * provider_type" and there was no way to discover the spelling. Nothing that follows creation — the primary
 * location, the documents, the contract, the activation itself — was reachable from any screen.</p>
 *
 * <p>It is now the worklist it should have been: the providers that are not live, what each is waiting on,
 * and the controls to finish them.</p>
 */
export function NetworkOnboarding() {
  const api = useApi();
  const t = useLoc();
  const { session } = useAuth();
  const mayWrite = mayAdministerTheNetwork(session?.issuerRoles);

  const state = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  const [creating, setCreating] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [announce, setAnnounce] = useState("");

  // "Not live" is the definition that matters: a Suspended provider that has to be brought back is the same
  // job as one that has never been live, and hiding it behind an onboarding-state filter loses it.
  const pending = useMemo(
    () => (state.data ?? []).filter((p) => p.onboardingState !== "Activated"),
    [state.data],
  );

  const columns: Column<ProviderSummary>[] = [
    { key: "provider", header: t(S.provider), cell: (r) => r.legalName, sortable: true, sortValue: (r) => r.legalName },
    { key: "code", header: t(S.codeH), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    { key: "type", header: t(S.typeH), cell: (r) => r.providerType },
    { key: "state", header: t(S.onboarding), cell: (r) => <StatusChip kind="neu" label={r.onboardingState} />, sortable: true, sortValue: (r) => r.onboardingState },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.onboardTitle)} />
      <p className="pol-muted">{t(S.onboardSubtitle)}</p>
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>

      {!mayWrite && <InlineAlert tone="info">{t(S.onboardingNotYours)}</InlineAlert>}

      {mayWrite && (
        <div className="screen-toolbar">
          <span />
          <Button variant="primary" leadingIcon={<Icon name="plus" />} onClick={() => setCreating(true)}>
            {t(S.onboardNew)}
          </Button>
        </div>
      )}

      <Card as="section">
        <div className="pol-stack">
          <div className="pay-head">
            <h2 style={{ margin: 0 }}>{t(S.inProgress)}</h2>
            <p className="pol-muted" style={{ margin: 0 }}>{t(S.inProgressHint)}</p>
          </div>
          <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.dirEmpty}>
            {() => (
              <DataTable
                columns={columns}
                rows={pending}
                rowKey={(r) => r.id}
                caption={t(S.inProgress)}
                emptyLabel={t(S.allLive)}
                interactive
                selectedKey={selected}
                onSelect={(r) => setSelected(r.id)}
              />
            )}
          </AsyncSection>
        </div>
      </Card>

      {selected && (
        <ProviderRecord providerId={selected} mayWrite={mayWrite} onChanged={() => state.reload()} />
      )}

      {creating && (
        <OnboardProviderForm
          onClose={() => setCreating(false)}
          onCreated={(id) => {
            setCreating(false);
            setSelected(id);
            setAnnounce(t(S.created));
            state.reload();
          }}
        />
      )}
    </div>
  );
}

function OnboardProviderForm({
  onClose, onCreated,
}: {
  onClose: () => void;
  onCreated: (providerId: string) => void;
}) {
  const t = useLoc();
  const api = useApi();
  const [key, rotate] = useIdempotencyKey();
  const [code, setCode] = useState("");
  const [legalName, setLegalName] = useState("");
  // A PICKER, not a text box. The old field was free text checked against an enum the operator could not
  // see: "hospital" failed with "unknown provider_type" and the only way to learn the spelling was to guess.
  const [type, setType] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  const complete = code.trim() !== "" && legalName.trim() !== "" && type !== null;

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(S.onboardNew)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button
            variant="primary"
            leadingIcon={<Icon name="check2" />}
            loading={busy}
            disabled={!complete}
            onClick={async () => {
              setBusy(true);
              setProblem(null);
              try {
                const created = await api.createProvider({
                  code: code.trim(),
                  legalName: legalName.trim(),
                  providerType: type as "Hospital",
                }, key);
                onCreated(created.id);
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
          label={t(S.code)}
          help={t(S.codeHint)}
          value={code}
          onChange={(e) => setCode(e.currentTarget.value)}
          autoComplete="off"
          className="tnum"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.legalName)}
          value={legalName}
          onChange={(e) => setLegalName(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <ComboboxField
          label={t(S.type)}
          options={PROVIDER_TYPES.map((v) => ({ value: v, label: t(TYPE_LABEL[v] ?? { en: v, ar: v }) }))}
          value={type}
          onChange={setType}
          required
        />
      </div>
    </Modal>
  );
}
