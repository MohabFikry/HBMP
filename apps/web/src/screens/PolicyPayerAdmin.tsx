import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button, Card, ComboboxField, DataTableView, Icon, InlineAlert, InputField, KpiList, Modal,
  StatusChip, TextareaField, useTableQuery,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  PayerBook, PayerContact, PayerContacts, PayerDetail, PayerHistoryEntry, PayerView, PayerWrite, PolicyApi,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";
import { writeErrorMessage } from "../api/writeError";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerBenefitProduct } from "../authz/permissions";
import { PageHeader, fillLocalized, useLoc, readErrorMessage } from "./_shared";
import { ConfirmAction } from "./ConfirmAction";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { useTheme } from "@mersal/design-system";

/** ONE client for the module, not one per render — a fresh instance per render turns a load effect keyed on
 *  it into an unbounded request loop (QA P0-1). */
const httpPolicyApi = createHttpPolicyApi();

/**
 * Phase 19.7 — payer administration (design 56).
 *
 * ============================================================================================================
 * WHAT THIS SCREEN IS FOR
 * ============================================================================================================
 * A payer is the counterparty a policy is funded BY, and the top of the commercial hierarchy the whole benefit
 * book hangs from. This screen was a four-column read-only table over a row that held a code, two names, a
 * type and a status — enough to LABEL a payer and not enough to administer one. There was no way to correct a
 * name, record an agreement, switch a payer off, or ask what had changed. Everything an operator actually
 * needed lived in an inbox, a signed PDF and a spreadsheet.
 *
 * ============================================================================================================
 * MASTER LIST, THEN ONE PAYER IN FULL
 * ============================================================================================================
 * The list answers "which payer" and nothing else — code, name, type, whether the funding is still running,
 * how much hangs off it, whether the record is live. Everything else is in the detail, because a payer has
 * four kinds of fact (identity, agreement, money, people) and a table with sixteen columns is a table nobody
 * reads. Selecting a row is the only navigation; there is no separate route, so a deep link is not something
 * this screen promises and then breaks.
 *
 * ============================================================================================================
 * WHAT THE ROLE MAY DO IS ABSENT, NOT DISABLED
 * ============================================================================================================
 * `policy:admin` is what the server requires for every write here, and it is held by three roles. A reader
 * (claims, finance, network) reaches this screen legitimately — they adjudicate against these terms — and is
 * shown no New / Edit / Deactivate control at all rather than four buttons that answer 403. A disabled button
 * teaches an operator that the screen is broken; an absent one teaches them whose job it is.
 *
 * ============================================================================================================
 * THE COMMERCIAL BLOCK IS WITHHELD WHOLE
 * ============================================================================================================
 * `terms` arrives as `null` — not as five nulls — for a caller who may not read contract terms, and the panel
 * says so in words. Five empty rows would read as "no ceiling recorded", which is a different and much worse
 * answer to give somebody about a ceiling that exists.
 */

const S = {
  title: { en: "Payers", ar: "الجهات الممولة" },
  subtitle: {
    en: "Who funds the cover — donors, government programmes, partner NGOs, insurers, and Mersal's own funds.",
    ar: "من يموّل التغطية — المانحون والبرامج الحكومية والمنظمات الشريكة وشركات التأمين وأموال مرسال الذاتية.",
  },
  // ── list ────────────────────────────────────────────────────────────────────────────────────────────────
  search: { en: "Search payers", ar: "بحث في الجهات الممولة" },
  searchHint: { en: "Code, name, or agreement", ar: "الرمز أو الاسم أو الاتفاقية" },
  code: { en: "Code", ar: "الرمز" },
  name: { en: "Name", ar: "الاسم" },
  type: { en: "Type", ar: "النوع" },
  status: { en: "Status", ar: "الحالة" },
  agreement: { en: "Agreement", ar: "الاتفاقية" },
  book: { en: "Book of business", ar: "محفظة الأعمال" },
  noPayers: { en: "No payers configured.", ar: "لا توجد جهات ممولة." },
  noMatches: { en: "No payer matches your search.", ar: "لا توجد جهة ممولة مطابقة لبحثك." },
  selectPayer: { en: "Select a payer to see its agreement, terms, contacts and book of business.", ar: "اختر جهة ممولة لعرض اتفاقيتها وشروطها وجهات الاتصال ومحفظة الأعمال." },
  filterStatus: { en: "Status", ar: "الحالة" },
  filterType: { en: "Type", ar: "النوع" },
  filterAgreement: { en: "Agreement", ar: "الاتفاقية" },
  // ── payer types ─────────────────────────────────────────────────────────────────────────────────────────
  typeSelfFunded: { en: "Self-funded", ar: "تمويل ذاتي" },
  typeDonor: { en: "Donor", ar: "جهة مانحة" },
  typeGovernment: { en: "Government", ar: "جهة حكومية" },
  typePartnerNGO: { en: "Partner NGO", ar: "منظمة شريكة" },
  typeInsurer: { en: "Insurer", ar: "شركة تأمين" },
  // ── agreement states ────────────────────────────────────────────────────────────────────────────────────
  stateUnrecorded: { en: "Not recorded", ar: "غير مسجّلة" },
  stateNotYetStarted: { en: "Starts later", ar: "تبدأ لاحقًا" },
  stateInForce: { en: "In force", ar: "سارية" },
  stateExpired: { en: "Expired", ar: "منتهية" },
  expiredWhileActive: {
    en: "This payer is active and its funding agreement has expired. Renew the agreement, or deactivate the payer once its policies are closed.",
    ar: "هذه الجهة نشطة وقد انتهت اتفاقية تمويلها. جدّد الاتفاقية أو أوقف الجهة بعد إغلاق وثائقها.",
  },
  // ── statuses ────────────────────────────────────────────────────────────────────────────────────────────
  statusActive: { en: "Active", ar: "نشطة" },
  statusInactive: { en: "Inactive", ar: "موقوفة" },
  // ── detail ──────────────────────────────────────────────────────────────────────────────────────────────
  identity: { en: "Identity", ar: "التعريف" },
  terms: { en: "Agreement & funding", ar: "الاتفاقية والتمويل" },
  contacts: { en: "Contacts", ar: "جهات الاتصال" },
  notes: { en: "Notes", ar: "ملاحظات" },
  externalRef: { en: "Payer's own reference", ar: "المرجع لدى الجهة" },
  externalRefHint: {
    en: "The grant or licence number the payer knows this by. Reconciliation is done against their reference, not ours.",
    ar: "رقم المنحة أو الترخيص الذي تعرفه الجهة. تتم التسوية وفق مرجعهم لا مرجعنا.",
  },
  agreementNo: { en: "Agreement number", ar: "رقم الاتفاقية" },
  agreementFrom: { en: "Funding from", ar: "التمويل من" },
  agreementTo: { en: "Until (exclusive)", ar: "حتى (غير شامل)" },
  ceiling: { en: "Funding ceiling", ar: "سقف التمويل" },
  ceilingHint: {
    en: "What the payer has committed. Leave it empty for uncapped — zero is not 'uncapped', it is 'funded for nothing'.",
    ar: "ما التزمت به الجهة. اتركه فارغًا لغير محدود — الصفر ليس «غير محدود» بل «تمويل بلا شيء».",
  },
  currency: { en: "Currency", ar: "العملة" },
  settlement: { en: "Settlement terms (days)", ar: "شروط السداد (أيام)" },
  cadence: { en: "Invoicing", ar: "إصدار الفواتير" },
  submissionWindow: { en: "Claim submission window (days)", ar: "مهلة تقديم المطالبات (أيام)" },
  submissionWindowHint: {
    en: "How long after the service date a claim may still reach this payer. Past it the money is gone whether or not the care was covered.",
    ar: "المدة المسموح بها بعد تاريخ الخدمة لتصل المطالبة إلى هذه الجهة. بعدها يضيع المبلغ سواء كانت الرعاية مغطاة أم لا.",
  },
  cadenceOnClaim: { en: "Per claim", ar: "لكل مطالبة" },
  cadenceMonthly: { en: "Monthly", ar: "شهريًا" },
  cadenceQuarterly: { en: "Quarterly", ar: "ربع سنوي" },
  cadenceSemiAnnual: { en: "Twice a year", ar: "نصف سنوي" },
  cadenceAnnual: { en: "Annually", ar: "سنويًا" },
  termsRestricted: {
    en: "Funding and settlement terms are restricted for your role. They are recorded — you are not being shown them.",
    ar: "شروط التمويل والسداد مقيّدة حسب دورك. وهي مسجّلة، لكنها غير معروضة لك.",
  },
  cannotSaveWithheldTerms: {
    en: "This payer cannot be saved from your role: its funding and settlement terms are not shown to you, and saving would overwrite them.",
    ar: "لا يمكن حفظ هذه الجهة من دورك: شروط التمويل والسداد غير معروضة لك، والحفظ سيستبدلها.",
  },
  amountsRestricted: { en: "Restricted for your role", ar: "مقيّد حسب دورك" },
  // ── contacts ────────────────────────────────────────────────────────────────────────────────────────────
  contactPrimary: { en: "Day-to-day contact", ar: "جهة الاتصال اليومية" },
  contactFinance: { en: "Settlement contact", ar: "جهة اتصال السداد" },
  contactEscalation: { en: "Escalation contact", ar: "جهة التصعيد" },
  contactsHint: {
    en: "Three named roles, because the three questions asked of a payer are asked of different people. These are the payer's own staff — never beneficiary details.",
    ar: "ثلاثة أدوار محددة، لأن الأسئلة الثلاثة التي تُطرح على الجهة تُطرح على أشخاص مختلفين. هؤلاء موظفو الجهة الممولة — وليست بيانات مستفيدين.",
  },
  noContacts: { en: "No contacts recorded.", ar: "لا توجد جهات اتصال مسجّلة." },
  contactName: { en: "Name", ar: "الاسم" },
  contactTitle: { en: "Role", ar: "الصفة" },
  contactEmail: { en: "Email", ar: "البريد الإلكتروني" },
  contactPhone: { en: "Phone", ar: "الهاتف" },
  // ── book of business ────────────────────────────────────────────────────────────────────────────────────
  policies: { en: "Policies", ar: "الوثائق" },
  activePolicies: { en: "Active policies", ar: "الوثائق النشطة" },
  members: { en: "Members", ar: "الأعضاء" },
  activeMembers: { en: "Active members", ar: "الأعضاء النشطون" },
  plans: { en: "Plans in use", ar: "الخطط المستخدمة" },
  committed: { en: "Committed", ar: "الملتزم به" },
  consumed: { en: "Consumed", ar: "المستهلك" },
  ofCeiling: { en: "{0}% of the ceiling committed", ar: "{0}٪ من السقف ملتزم به" },
  ceilingExceeded: {
    en: "Committed cover exceeds the funding ceiling on this agreement.",
    ar: "التغطية الملتزم بها تتجاوز سقف التمويل في هذه الاتفاقية.",
  },
  lastChanged: { en: "Last changed", ar: "آخر تعديل" },
  by: { en: "by {0}", ar: "بواسطة {0}" },
  // ── controls ────────────────────────────────────────────────────────────────────────────────────────────
  newPayer: { en: "New payer", ar: "جهة ممولة جديدة" },
  edit: { en: "Edit this payer", ar: "تعديل هذه الجهة" },
  deactivate: { en: "Deactivate this payer", ar: "إيقاف هذه الجهة" },
  reactivate: { en: "Reactivate this payer", ar: "إعادة تنشيط هذه الجهة" },
  history: { en: "Change history", ar: "سجل التغييرات" },
  save: { en: "Save", ar: "حفظ" },
  create: { en: "Create payer", ar: "إنشاء جهة ممولة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  // ── the form ────────────────────────────────────────────────────────────────────────────────────────────
  formCreateTitle: { en: "New payer", ar: "جهة ممولة جديدة" },
  formEditTitle: { en: "Edit payer", ar: "تعديل الجهة الممولة" },
  payerCode: { en: "Payer code", ar: "رمز الجهة" },
  codeLocked: {
    en: "The code can never be changed. Extracts, reconciliation files and the payer's own systems join on it — renaming it re-points every one of them at a payer they will no longer find. To replace a code, create the right payer and move its policies deliberately.",
    ar: "لا يمكن تغيير الرمز أبدًا. فالمستخرجات وملفات التسوية وأنظمة الجهة نفسها ترتبط به — وتغييره يوجّهها جميعًا إلى جهة لن تجدها. لاستبدال الرمز، أنشئ الجهة الصحيحة وانقل وثائقها عمدًا.",
  },
  nameEn: { en: "Name (English)", ar: "الاسم (إنجليزي)" },
  nameAr: { en: "Name (Arabic)", ar: "الاسم (عربي)" },
  needCode: { en: "A payer code is required.", ar: "رمز الجهة مطلوب." },
  needNames: {
    en: "A payer needs a name in both languages: half the platform renders in Arabic.",
    ar: "تحتاج الجهة إلى اسم بكلتا اللغتين: نصف المنصة يُعرض بالعربية.",
  },
  needWindow: { en: "The agreement must end after it starts.", ar: "يجب أن تنتهي الاتفاقية بعد بدايتها." },
  needPositiveCeiling: {
    en: "A ceiling of zero is not 'uncapped'. Leave it empty instead.",
    ar: "سقف بقيمة صفر ليس «غير محدود». اتركه فارغًا بدلًا من ذلك.",
  },
  created: { en: "Payer created.", ar: "تم إنشاء الجهة الممولة." },
  updated: { en: "Payer updated.", ar: "تم تحديث الجهة الممولة." },
  // ── status change ───────────────────────────────────────────────────────────────────────────────────────
  deactivateTitle: { en: "Deactivate {0}?", ar: "إيقاف {0}؟" },
  deactivateBody: {
    en: "{0} stops being offered as a funder for new policies. Nothing already enrolled is changed, and the record stays readable forever.",
    ar: "لن تُعرض {0} كجهة تمويل للوثائق الجديدة. لا يتغيّر أي تسجيل قائم، ويبقى السجل قابلًا للقراءة دائمًا.",
  },
  deactivateReversible: { en: "It can be reactivated at any time.", ar: "يمكن إعادة تنشيطها في أي وقت." },
  reactivateTitle: { en: "Reactivate {0}?", ar: "إعادة تنشيط {0}؟" },
  reactivateBody: {
    en: "{0} becomes available again as a funder for new policies.",
    ar: "تصبح {0} متاحة مجددًا كجهة تمويل للوثائق الجديدة.",
  },
  reason: { en: "Why", ar: "السبب" },
  reasonHint: {
    en: "In a sentence somebody reading this record next year would understand. It is stored on the payer and on the change history.",
    ar: "بجملة يفهمها من يقرأ هذا السجل بعد عام. تُحفَظ على الجهة وفي سجل التغييرات.",
  },
  reasonTooShort: { en: "Say why, in a sentence.", ar: "اذكر السبب في جملة." },
  deactivated: { en: "Payer deactivated.", ar: "تم إيقاف الجهة الممولة." },
  reactivated: { en: "Payer reactivated.", ar: "تمت إعادة تنشيط الجهة الممولة." },
  // ── history ─────────────────────────────────────────────────────────────────────────────────────────────
  historyTitle: { en: "Change history — {0}", ar: "سجل التغييرات — {0}" },
  historyHint: {
    en: "Every create and edit, newest first. This is the operational record kept beside the payer; the tamper-evident audit trail is separate and belongs to Compliance.",
    ar: "كل إنشاء وتعديل، الأحدث أولًا. هذا هو السجل التشغيلي المحفوظ مع الجهة؛ أما سجل التدقيق غير القابل للعبث فمنفصل ويخص الالتزام.",
  },
  historyWhen: { en: "When", ar: "متى" },
  historyWho: { en: "Who", ar: "من" },
  historyWhat: { en: "What it said", ar: "ما كان مسجّلًا" },
  historyCreated: { en: "Created", ar: "أُنشئت" },
  historyChanged: { en: "Changed", ar: "عُدّلت" },
  noHistory: { en: "No history recorded.", ar: "لا يوجد سجل." },
  unknownActor: { en: "Not recorded", ar: "غير مسجّل" },
  close: { en: "Close", ar: "إغلاق" },
} satisfies Record<string, Localized>;

const PAYER_TYPES = ["SelfFunded", "Donor", "Government", "PartnerNGO", "Insurer"] as const;
const CADENCES = ["OnClaim", "Monthly", "Quarterly", "SemiAnnual", "Annual"] as const;

const TYPE_LABEL: Record<string, Localized> = {
  SelfFunded: S.typeSelfFunded, Donor: S.typeDonor, Government: S.typeGovernment,
  PartnerNGO: S.typePartnerNGO, Insurer: S.typeInsurer,
};
const STATE_LABEL: Record<string, Localized> = {
  Unrecorded: S.stateUnrecorded, NotYetStarted: S.stateNotYetStarted,
  InForce: S.stateInForce, Expired: S.stateExpired,
};
/** Four cues, never colour alone — the chip supplies icon and shape from the kind. */
const STATE_KIND: Record<string, "ok" | "info" | "warn" | "neu"> = {
  Unrecorded: "neu", NotYetStarted: "info", InForce: "ok", Expired: "warn",
};
const CADENCE_LABEL: Record<string, Localized> = {
  OnClaim: S.cadenceOnClaim, Monthly: S.cadenceMonthly, Quarterly: S.cadenceQuarterly,
  SemiAnnual: S.cadenceSemiAnnual, Annual: S.cadenceAnnual,
};

// ────────────────────────────────────────────────────────────────────────────────────────────────────────
// The screen
// ────────────────────────────────────────────────────────────────────────────────────────────────────────

export function PolicyPayers({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const { session } = useAuth();
  const mayWrite = mayAdministerBenefitProduct(session?.role ?? undefined);

  const [rows, setRows] = useState<PayerView[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<PayerDetail | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  const [form, setForm] = useState<{ mode: "create" } | { mode: "edit"; payer: PayerView } | null>(null);
  const [statusChange, setStatusChange] = useState<"deactivate" | "reactivate" | null>(null);
  const [historyFor, setHistoryFor] = useState<PayerView | null>(null);

  const load = useCallback(async () => {
    try {
      setRows(await api.payers());
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api]);

  useEffect(() => { void load(); }, [load]);

  // The detail is a second read rather than a slice of the list, because the book of business is a set of
  // aggregates the list cannot afford to compute for every row.
  useEffect(() => {
    if (!selected) { setDetail(null); return; }
    let live = true;
    setDetail(null);
    api.payer(selected)
      .then((d) => { if (live) setDetail(d); })
      .catch((e) => { if (live) setError(readErrorMessage(e)); });
    return () => { live = false; };
  }, [api, selected]);

  const reload = useCallback(async (id: string) => {
    await load();
    try { setDetail(await api.payer(id)); } catch (e) { setError(readErrorMessage(e)); }
  }, [api, load]);

  const columns: Column<PayerView>[] = [
    {
      key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.payerCode}</span>,
      sortable: true, sortValue: (r) => r.payerCode,
    },
    { key: "name", header: t(S.name), cell: (r) => <BiName en={r.nameEn} ar={r.nameAr} /> },
    {
      key: "type", header: t(S.type), cell: (r) => t(TYPE_LABEL[r.payerType] ?? { en: r.payerType, ar: r.payerType }),
      sortable: true, sortValue: (r) => r.payerType,
    },
    { key: "agreement", header: t(S.agreement), cell: (r) => <AgreementCell payer={r} /> },
    {
      key: "status", header: t(S.status),
      cell: (r) => (
        <StatusChip
          kind={r.status === "Active" ? "ok" : "neu"}
          label={t(r.status === "Active" ? S.statusActive : S.statusInactive)}
        />
      ),
      sortable: true, sortValue: (r) => r.status,
    },
  ];

  const query = useTableQuery<PayerView>({
    rows: rows ?? [],
    columns,
    searchText: (r) => `${r.payerCode} ${r.nameEn} ${r.nameAr} ${r.agreement.agreementNo ?? ""} ${r.agreement.externalRef ?? ""}`,
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    filters: [
      {
        key: "status",
        label: t(S.filterStatus),
        options: [
          { value: "Active", label: t(S.statusActive) },
          { value: "Inactive", label: t(S.statusInactive) },
        ],
        match: (r, v) => r.status === v,
      },
      {
        key: "type",
        label: t(S.filterType),
        options: PAYER_TYPES.map((k) => ({ value: k, label: t(TYPE_LABEL[k] ?? S.type) })),
        match: (r, v) => r.payerType === v,
      },
      {
        key: "agreement",
        label: t(S.filterAgreement),
        options: [
          { value: "InForce", label: t(S.stateInForce) },
          { value: "Expired", label: t(S.stateExpired) },
          { value: "Unrecorded", label: t(S.stateUnrecorded) },
        ],
        match: (r, v) => r.agreement.state === v,
      },
    ],
    initialSortKey: "code",
    persistKey: "policy-payers",
  });

  const current = rows?.find((r) => r.payerId === selected) ?? null;

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <p className="pol-muted">{t(S.subtitle)}</p>
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {mayWrite && (
        <div className="screen-toolbar">
          <span />
          <Button variant="primary" leadingIcon={<Icon name="plus" />} onClick={() => setForm({ mode: "create" })}>
            {t(S.newPayer)}
          </Button>
        </div>
      )}

      <Card>
        <DataTableView
          query={query}
          columns={columns}
          rowKey={(r) => r.payerId}
          caption={t(S.title)}
          interactive
          selectedKey={selected}
          onSelect={(r) => setSelected(r.payerId)}
          loading={rows === null && !error}
          emptyLabel={t(S.noPayers)}
          noMatchesLabel={t(S.noMatches)}
        />
      </Card>

      {!selected && <InlineAlert tone="info">{t(S.selectPayer)}</InlineAlert>}

      {current && (
        <PayerDetailPane
          payer={detail?.payer ?? current}
          book={detail?.book ?? null}
          mayWrite={mayWrite}
          onEdit={() => setForm({ mode: "edit", payer: detail?.payer ?? current })}
          onStatus={() => setStatusChange(current.status === "Active" ? "deactivate" : "reactivate")}
          onHistory={() => setHistoryFor(detail?.payer ?? current)}
        />
      )}

      {form && (
        <PayerForm
          api={api}
          mode={form.mode}
          payer={form.mode === "edit" ? form.payer : null}
          onClose={() => setForm(null)}
          onSaved={async (saved, wasCreate) => {
            setForm(null);
            setSelected(saved.payerId);
            setAnnounce(t(wasCreate ? S.created : S.updated));
            await reload(saved.payerId);
          }}
        />
      )}

      {current && statusChange && (
        <StatusChangeDialog
          api={api}
          payer={current}
          intent={statusChange}
          onClose={() => setStatusChange(null)}
          onDone={async () => {
            setStatusChange(null);
            setAnnounce(t(statusChange === "deactivate" ? S.deactivated : S.reactivated));
            await reload(current.payerId);
          }}
        />
      )}

      {historyFor && (
        <PayerHistoryModal api={api} payer={historyFor} onClose={() => setHistoryFor(null)} />
      )}
    </div>
  );
}

function BiName({ en, ar }: { en: string; ar: string }) {
  const t = useLoc();
  return <>{t({ en, ar })}</>;
}

/** The list's agreement column: state as a four-cue chip, with the window beneath it when there is one. */
function AgreementCell({ payer }: { payer: PayerView }) {
  const t = useLoc();
  const fmt = useFormat();
  const state = payer.agreement.state;
  return (
    <div className="pay-agreement-cell">
      <StatusChip kind={STATE_KIND[state] ?? "neu"} label={t(STATE_LABEL[state] ?? { en: state, ar: state })} />
      {(payer.agreement.agreementFrom || payer.agreement.agreementTo) && (
        <span className="pol-muted">
          {fmt.date(payer.agreement.agreementFrom)} → {payer.agreement.agreementTo ? fmt.date(payer.agreement.agreementTo) : "—"}
        </span>
      )}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────────────────────────────────────
// The detail
// ────────────────────────────────────────────────────────────────────────────────────────────────────────

function PayerDetailPane({
  payer, book, mayWrite, onEdit, onStatus, onHistory,
}: {
  payer: PayerView;
  book: PayerBook | null;
  mayWrite: boolean;
  onEdit: () => void;
  onStatus: () => void;
  onHistory: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const active = payer.status === "Active";

  return (
    <div className="pol-detail">
      <Card>
        <div className="screen-toolbar">
          <div className="pay-head">
            <h3>
              <BiName en={payer.nameEn} ar={payer.nameAr} />{" "}
              <span className="tnum pol-muted">{payer.payerCode}</span>
            </h3>
            <div className="pay-chips">
              <StatusChip kind={active ? "ok" : "neu"} label={t(active ? S.statusActive : S.statusInactive)} />
              <StatusChip
                kind={STATE_KIND[payer.agreement.state] ?? "neu"}
                label={t(STATE_LABEL[payer.agreement.state] ?? { en: payer.agreement.state, ar: payer.agreement.state })}
              />
              <span className="pol-muted">{t(TYPE_LABEL[payer.payerType] ?? { en: payer.payerType, ar: payer.payerType })}</span>
            </div>
          </div>
          <div className="rst-actions">
            {/* Icon-only: the glyph is the control, so each carries its own accessible name. */}
            <Button variant="ghost" aria-label={t(S.history)} title={t(S.history)} onClick={onHistory}>
              <Icon name="history" />
            </Button>
            {mayWrite && (
              <>
                <Button variant="ghost" aria-label={t(S.edit)} title={t(S.edit)} onClick={onEdit}>
                  <Icon name="pen" />
                </Button>
                <Button
                  variant="ghost"
                  aria-label={t(active ? S.deactivate : S.reactivate)}
                  title={t(active ? S.deactivate : S.reactivate)}
                  onClick={onStatus}
                >
                  <Icon name={active ? "lock" : "undo"} />
                </Button>
              </>
            )}
          </div>
        </div>

        {/* The combination somebody has to act on, said in words rather than left to be inferred from two
            chips that happen to disagree. */}
        {active && payer.agreement.state === "Expired" && (
          <InlineAlert tone="warn">{t(S.expiredWhileActive)}</InlineAlert>
        )}
        {!active && payer.statusReason && (
          <InlineAlert tone="info">
            {payer.statusReason}
            {payer.statusChangedAt ? ` — ${fmt.dateTime(payer.statusChangedAt)}` : ""}
          </InlineAlert>
        )}

        {book && <BookOfBusiness payer={payer} book={book} />}
      </Card>

      <Card>
        <h3>{t(S.terms)}</h3>
        <AgreementFacts payer={payer} />
      </Card>

      <Card>
        <h3>{t(S.contacts)}</h3>
        <p className="pol-muted">{t(S.contactsHint)}</p>
        <ContactList contacts={payer.contacts ?? null} />
      </Card>

      {payer.notes && (
        <Card>
          <h3>{t(S.notes)}</h3>
          <p>{payer.notes}</p>
        </Card>
      )}

      <p className="pol-muted">
        {t(S.lastChanged)}: {fmt.dateTime(payer.updatedAt)}
        {payer.updatedByName ? ` ${t(fillLocalized(S.by, payer.updatedByName))}` : ""}
      </p>
    </div>
  );
}

function BookOfBusiness({ payer, book }: { payer: PayerView; book: PayerBook }) {
  const t = useLoc();
  const fmt = useFormat();
  const currency = payer.terms?.currency ?? "EGP";
  const money = useMoney(currency);

  const items = [
    { label: t(S.policies), value: fmt.number(book.policyCount) },
    { label: t(S.activePolicies), value: fmt.number(book.activePolicyCount) },
    { label: t(S.members), value: fmt.number(book.memberCount) },
    { label: t(S.activeMembers), value: fmt.number(book.activeMemberCount) },
    { label: t(S.plans), value: fmt.number(book.planCount) },
    {
      label: t(S.committed),
      // `null` is "withheld", `0` is zero. Rendering both as "—" would tell a finance-blind role that a
      // payer with a book of business has none.
      value: book.committedLimit === null || book.committedLimit === undefined
        ? t(S.amountsRestricted)
        : money(book.committedLimit),
    },
  ];

  const pct = book.ceilingPercentCommitted;
  return (
    <>
      <KpiList items={items} />
      {typeof pct === "number" && (
        <p className={pct > 100 ? "" : "pol-muted"}>
          {t(fillLocalized(S.ofCeiling, fmt.number(pct, { maximumFractionDigits: 1 })))}
        </p>
      )}
      {typeof pct === "number" && pct > 100 && <InlineAlert tone="warn">{t(S.ceilingExceeded)}</InlineAlert>}
    </>
  );
}

function AgreementFacts({ payer }: { payer: PayerView }) {
  const t = useLoc();
  const fmt = useFormat();
  const a = payer.agreement;
  const terms = payer.terms;
  const money = useMoney(terms?.currency ?? "EGP");

  return (
    <>
      <dl className="pol-identity-list">
        <Fact label={t(S.externalRef)} value={a.externalRef ?? "—"} mono />
        <Fact label={t(S.agreementNo)} value={a.agreementNo ?? "—"} mono />
        <Fact label={t(S.agreementFrom)} value={fmt.date(a.agreementFrom)} />
        <Fact label={t(S.agreementTo)} value={a.agreementTo ? fmt.date(a.agreementTo) : "—"} />
      </dl>

      {/* Withheld as a BLOCK, and said so. Five empty rows would read as "no ceiling recorded", which is a
          different and worse answer about a ceiling that exists. */}
      {terms === null || terms === undefined ? (
        <InlineAlert tone="info">{t(S.termsRestricted)}</InlineAlert>
      ) : (
        <dl className="pol-identity-list">
          <Fact
            label={t(S.ceiling)}
            value={typeof terms.fundingCeiling === "number" ? money(terms.fundingCeiling) : "—"}
          />
          <Fact label={t(S.currency)} value={terms.currency} mono />
          <Fact
            label={t(S.settlement)}
            value={typeof terms.settlementTermsDays === "number" ? fmt.number(terms.settlementTermsDays) : "—"}
          />
          <Fact
            label={t(S.cadence)}
            value={terms.invoicingCadence ? t(CADENCE_LABEL[terms.invoicingCadence] ?? { en: terms.invoicingCadence, ar: terms.invoicingCadence }) : "—"}
          />
          <Fact
            label={t(S.submissionWindow)}
            value={typeof terms.claimSubmissionWindowDays === "number" ? fmt.number(terms.claimSubmissionWindowDays) : "—"}
          />
        </dl>
      )}
    </>
  );
}

function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd className={mono ? "tnum" : undefined}>{value}</dd>
    </div>
  );
}

function ContactList({ contacts }: { contacts: PayerContacts | null }) {
  const t = useLoc();
  const entries: Array<[Localized, PayerContact | null | undefined]> = [
    [S.contactPrimary, contacts?.primary],
    [S.contactFinance, contacts?.finance],
    [S.contactEscalation, contacts?.escalation],
  ];
  const present = entries.filter(([, c]) => c);
  if (present.length === 0) return <p className="pol-muted">{t(S.noContacts)}</p>;

  return (
    <ul className="pay-contacts">
      {present.map(([label, c]) => (
        <li key={label.en}>
          <span className="pay-contact-role">{t(label)}</span>
          <span className="pay-contact-name">{c?.name ?? "—"}</span>
          {c?.title && <span className="pol-muted">{c.title}</span>}
          {c?.email && (
            <a href={`mailto:${c.email}`}>
              <Icon name="doc" /> {c.email}
            </a>
          )}
          {c?.phone && (
            <a href={`tel:${c.phone}`}>
              <Icon name="phone" /> {c.phone}
            </a>
          )}
        </li>
      ))}
    </ul>
  );
}

/** Money in the payer's OWN currency. `useFormat().money` is fixed to EGP, and a USD grant rendered in
 *  pounds is not a formatting slip — it is a number somebody would act on. */
function useMoney(currency: string): (v: number) => string {
  const { lang } = useTheme();
  return useMemo(() => {
    const locale = lang === "ar" ? "ar-EG" : "en-GB";
    let fmt: Intl.NumberFormat;
    try {
      fmt = new Intl.NumberFormat(locale, { style: "currency", currency });
    } catch {
      // An unrecognised code must not blank the panel: show the number and the code beside it.
      return (v: number) => `${new Intl.NumberFormat(locale).format(v)} ${currency}`;
    }
    return (v: number) => fmt.format(v);
  }, [lang, currency]);
}

// ────────────────────────────────────────────────────────────────────────────────────────────────────────
// Create / edit
// ────────────────────────────────────────────────────────────────────────────────────────────────────────

function PayerForm({
  api, mode, payer, onClose, onSaved,
}: {
  api: PolicyApi;
  mode: "create" | "edit";
  payer: PayerView | null;
  onClose: () => void;
  onSaved: (saved: PayerView, wasCreate: boolean) => void | Promise<void>;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();

  const [payerCode, setPayerCode] = useState("");
  const [nameEn, setNameEn] = useState(payer?.nameEn ?? "");
  const [nameAr, setNameAr] = useState(payer?.nameAr ?? "");
  const [payerType, setPayerType] = useState(payer?.payerType ?? "Donor");
  const [externalRef, setExternalRef] = useState(payer?.agreement.externalRef ?? "");
  const [agreementNo, setAgreementNo] = useState(payer?.agreement.agreementNo ?? "");
  const [from, setFrom] = useState(payer?.agreement.agreementFrom ?? "");
  const [until, setUntil] = useState(payer?.agreement.agreementTo ?? "");
  const [ceiling, setCeiling] = useState(
    typeof payer?.terms?.fundingCeiling === "number" ? String(payer.terms.fundingCeiling) : "");
  const [currency, setCurrency] = useState(payer?.terms?.currency ?? "EGP");
  const [settlement, setSettlement] = useState(
    typeof payer?.terms?.settlementTermsDays === "number" ? String(payer.terms.settlementTermsDays) : "");
  const [cadence, setCadence] = useState(payer?.terms?.invoicingCadence ?? "");
  const [window, setWindow] = useState(
    typeof payer?.terms?.claimSubmissionWindowDays === "number" ? String(payer.terms.claimSubmissionWindowDays) : "");
  const [notes, setNotes] = useState(payer?.notes ?? "");
  const [primary, setPrimary] = useState<PayerContact>(payer?.contacts?.primary ?? {});
  const [finance, setFinance] = useState<PayerContact>(payer?.contacts?.finance ?? {});
  const [escalation, setEscalation] = useState<PayerContact>(payer?.contacts?.escalation ?? {});

  const [problem, setProblem] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);

  /**
   * Every role holding `policy:admin` is also a contract reader, so a form that cannot show the terms should
   * never be reachable. It is guarded anyway, because the server reads an ABSENT terms block as "clear
   * them" — and a save that silently wiped a funding ceiling because the person saving was not allowed to
   * see it is the worst thing this screen could do. If the unreachable case ever becomes reachable, the save
   * is refused loudly instead.
   */
  const termsReadable = mode === "create" || (payer?.terms !== null && payer?.terms !== undefined);

  const submit = async () => {
    const num = (s: string): number | null => (s.trim() === "" ? null : Number(s));
    if (!termsReadable) { setProblem(S.cannotSaveWithheldTerms); return; }
    if (mode === "create" && !payerCode.trim()) { setProblem(S.needCode); return; }
    if (!nameEn.trim() || !nameAr.trim()) { setProblem(S.needNames); return; }
    if (from && until && until <= from) { setProblem(S.needWindow); return; }
    const ceilingValue = num(ceiling);
    if (ceilingValue !== null && !(ceilingValue > 0)) { setProblem(S.needPositiveCeiling); return; }

    const body: PayerWrite = {
      nameEn: nameEn.trim(),
      nameAr: nameAr.trim(),
      payerType,
      notes: notes.trim() || null,
      contacts: { primary: clean(primary), finance: clean(finance), escalation: clean(escalation) },
      terms: {
            externalRef: externalRef.trim() || null,
            agreementNo: agreementNo.trim() || null,
            agreementFrom: from || null,
            agreementTo: until || null,
            fundingCeiling: ceilingValue,
            currency: currency.trim() || null,
            settlementTermsDays: num(settlement),
            invoicingCadence: cadence || null,
            claimSubmissionWindowDays: num(window),
      },
    };

    setBusy(true);
    setProblem(null);
    try {
      const saved = mode === "create"
        ? await api.createPayer({ ...body, payerCode: payerCode.trim() }, key)
        : await api.updatePayer(payer!.payerId, body);
      await onSaved(saved, mode === "create");
    } catch (e) {
      rotate();
      setProblem(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(mode === "create" ? S.formCreateTitle : S.formEditTitle)}
      closeLabel={t(S.close)}
      wide
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant="primary" onClick={() => void submit()} loading={busy}>
            {t(mode === "create" ? S.create : S.save)}
          </Button>
        </>
      }
    >
      {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}

      <section className="pay-form-section">
        <h4>{t(S.identity)}</h4>
        <div className="pay-form-grid">
          {mode === "create" ? (
            <InputField
              label={t(S.payerCode)}
              value={payerCode}
              onChange={(e) => setPayerCode(e.currentTarget.value)}
              help={t(S.codeLocked)}
              required
            />
          ) : (
            <InputField label={t(S.payerCode)} value={payer?.payerCode ?? ""} readOnly help={t(S.codeLocked)} />
          )}
          <ComboboxField
            label={t(S.type)}
            value={payerType}
            onChange={setPayerType}
            options={PAYER_TYPES.map((k) => ({ value: k, label: t(TYPE_LABEL[k] ?? { en: k, ar: k }) }))}
          />
          <InputField label={t(S.nameEn)} value={nameEn} onChange={(e) => setNameEn(e.currentTarget.value)} required />
          <InputField label={t(S.nameAr)} value={nameAr} onChange={(e) => setNameAr(e.currentTarget.value)} required dir="rtl" />
        </div>
      </section>

      {termsReadable ? (
        <section className="pay-form-section">
          <h4>{t(S.terms)}</h4>
          <div className="pay-form-grid">
            <InputField label={t(S.externalRef)} value={externalRef} onChange={(e) => setExternalRef(e.currentTarget.value)} help={t(S.externalRefHint)} />
            <InputField label={t(S.agreementNo)} value={agreementNo} onChange={(e) => setAgreementNo(e.currentTarget.value)} />
            <InputField label={t(S.agreementFrom)} type="date" value={from} onChange={(e) => setFrom(e.currentTarget.value)} />
            <InputField label={t(S.agreementTo)} type="date" value={until} onChange={(e) => setUntil(e.currentTarget.value)} />
            <InputField
              label={t(S.ceiling)} type="number" min={0} step="0.01" inputMode="decimal"
              value={ceiling} onChange={(e) => setCeiling(e.currentTarget.value)} help={t(S.ceilingHint)}
            />
            <InputField label={t(S.currency)} value={currency} onChange={(e) => setCurrency(e.currentTarget.value)} maxLength={3} />
            <InputField label={t(S.settlement)} type="number" min={0} max={365} value={settlement} onChange={(e) => setSettlement(e.currentTarget.value)} />
            <ComboboxField
              label={t(S.cadence)}
              value={cadence}
              onChange={setCadence}
              options={[{ value: "", label: "—" }, ...CADENCES.map((c) => ({ value: c, label: t(CADENCE_LABEL[c] ?? { en: c, ar: c }) }))]}
            />
            <InputField
              label={t(S.submissionWindow)} type="number" min={0} max={1095}
              value={window} onChange={(e) => setWindow(e.currentTarget.value)} help={t(S.submissionWindowHint)}
            />
          </div>
        </section>
      ) : (
        <InlineAlert tone="info">{t(S.termsRestricted)}</InlineAlert>
      )}

      <section className="pay-form-section">
        <h4>{t(S.contacts)}</h4>
        <p className="pol-muted">{t(S.contactsHint)}</p>
        <ContactFields label={t(S.contactPrimary)} value={primary} onChange={setPrimary} />
        <ContactFields label={t(S.contactFinance)} value={finance} onChange={setFinance} />
        <ContactFields label={t(S.contactEscalation)} value={escalation} onChange={setEscalation} />
      </section>

      <section className="pay-form-section">
        <TextareaField label={t(S.notes)} value={notes} rows={3} onChange={(e) => setNotes(e.currentTarget.value)} />
      </section>
    </Modal>
  );
}

function ContactFields({
  label, value, onChange,
}: { label: string; value: PayerContact; onChange: (c: PayerContact) => void }) {
  const t = useLoc();
  return (
    <fieldset className="pay-contact-fields">
      <legend>{label}</legend>
      <div className="pay-form-grid">
        <InputField label={t(S.contactName)} value={value.name ?? ""} onChange={(e) => onChange({ ...value, name: e.currentTarget.value })} />
        <InputField label={t(S.contactTitle)} value={value.title ?? ""} onChange={(e) => onChange({ ...value, title: e.currentTarget.value })} />
        <InputField label={t(S.contactEmail)} type="email" value={value.email ?? ""} onChange={(e) => onChange({ ...value, email: e.currentTarget.value })} />
        <InputField label={t(S.contactPhone)} type="tel" value={value.phone ?? ""} onChange={(e) => onChange({ ...value, phone: e.currentTarget.value })} />
      </div>
    </fieldset>
  );
}

/** An entry with nothing in it is not a contact — it is sent as null so it comes back absent rather than as
 *  a card with a heading and four blank rows. */
function clean(c: PayerContact): PayerContact | null {
  const any = [c.name, c.title, c.email, c.phone].some((v) => (v ?? "").trim() !== "");
  return any
    ? {
        name: c.name?.trim() || null, title: c.title?.trim() || null,
        email: c.email?.trim() || null, phone: c.phone?.trim() || null,
      }
    : null;
}

// ────────────────────────────────────────────────────────────────────────────────────────────────────────
// Deactivate / reactivate
// ────────────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * The reason is REQUIRED, and the dialog cannot be cleared without one — the confirm stays unpressable until
 * it reads like a sentence. The server enforces the same rule; this is the half that explains it before the
 * operator has typed anything, rather than after they have pressed the button.
 */
function StatusChangeDialog({
  api, payer, intent, onClose, onDone,
}: {
  api: PolicyApi;
  payer: PayerView;
  intent: "deactivate" | "reactivate";
  onClose: () => void;
  onDone: () => void | Promise<void>;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<Localized | null>(null);
  const name = useLoc()({ en: payer.nameEn, ar: payer.nameAr });
  const deactivating = intent === "deactivate";

  return (
    <ConfirmAction
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={fillLocalized(deactivating ? S.deactivateTitle : S.reactivateTitle, name)}
      body={fillLocalized(deactivating ? S.deactivateBody : S.reactivateBody, name)}
      description={S.deactivateReversible}
      confirmLabel={deactivating ? S.deactivate : S.reactivate}
      canConfirm={reason.trim().length >= 10}
      onConfirm={async () => {
        try {
          if (deactivating) await api.deactivatePayer(payer.payerId, reason.trim(), key);
          else await api.reactivatePayer(payer.payerId, reason.trim(), key);
          await onDone();
        } catch (e) {
          rotate();
          // The 409 that says how many active policies still hang off this payer arrives here verbatim, and
          // it is the whole point of the refusal: an unexplained "no" sends somebody looking for a bug.
          setProblem(writeErrorMessage(e).message);
          // Re-thrown so ConfirmAction keeps the dialog open. Resolving here would dismiss it — and a
          // dialog that closes on a refusal reads as "done", with the typed reason thrown away.
          throw e;
        }
      }}
    >
      {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
      <TextareaField
        label={t(S.reason)}
        help={t(S.reasonHint)}
        rows={3}
        value={reason}
        onChange={(e) => setReason(e.currentTarget.value)}
        error={reason.trim().length > 0 && reason.trim().length < 10 ? t(S.reasonTooShort) : undefined}
        required
      />
    </ConfirmAction>
  );
}

// ────────────────────────────────────────────────────────────────────────────────────────────────────────
// History
// ────────────────────────────────────────────────────────────────────────────────────────────────────────

function PayerHistoryModal({ api, payer, onClose }: { api: PolicyApi; payer: PayerView; onClose: () => void }) {
  const t = useLoc();
  const fmt = useFormat();
  const [entries, setEntries] = useState<PayerHistoryEntry[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const name = t({ en: payer.nameEn, ar: payer.nameAr });
  const money = useMoney(payer.terms?.currency ?? "EGP");

  useEffect(() => {
    let live = true;
    api.payerHistory(payer.payerId)
      .then((p) => { if (live) setEntries(p.entries); })
      .catch((e) => { if (live) setError(readErrorMessage(e)); });
    return () => { live = false; };
  }, [api, payer.payerId]);

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(fillLocalized(S.historyTitle, name))}
      closeLabel={t(S.close)}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>}
    >
      <p className="pol-muted">{t(S.historyHint)}</p>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {entries !== null && entries.length === 0 && <p className="pol-muted">{t(S.noHistory)}</p>}
      {entries && entries.length > 0 && (
        <ol className="pay-history">
          {entries.map((e) => (
            <li key={e.historyId}>
              <div className="pay-history-when">
                <StatusChip
                  kind={e.operation === "INSERT" ? "info" : "neu"}
                  label={t(e.operation === "INSERT" ? S.historyCreated : S.historyChanged)}
                />
                <span>{fmt.dateTime(e.recordedAt)}</span>
                <span className="pol-muted">{e.actorName ?? t(S.unknownActor)}</span>
              </div>
              <dl className="pol-identity-list">
                <Fact label={t(S.name)} value={e.nameEn} />
                <Fact label={t(S.type)} value={t(TYPE_LABEL[e.payerType] ?? { en: e.payerType, ar: e.payerType })} />
                <Fact label={t(S.status)} value={t(e.status === "Active" ? S.statusActive : S.statusInactive)} />
                {typeof e.fundingCeiling === "number" && (
                  <Fact label={t(S.ceiling)} value={money(e.fundingCeiling)} />
                )}
                {e.agreementNo && <Fact label={t(S.agreementNo)} value={e.agreementNo} mono />}
              </dl>
              {e.statusReason && <p className="pol-muted">{e.statusReason}</p>}
            </li>
          ))}
        </ol>
      )}
    </Modal>
  );
}
