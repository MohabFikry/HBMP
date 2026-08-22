import { useCallback, useMemo, useState } from "react";
import {
  Button, Card, ComboboxField, DataTable, DataTableView, Icon, InlineAlert, InputField, Modal,
  StatusChip, useTableQuery,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  ContractAdmin, ContractWrite, Localized, ProviderSummary, ServiceLine,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerTheNetwork } from "../authz/permissions";
import { writeErrorMessage } from "../api/writeError";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, fillLocalized, useLoc } from "./_shared";
import { RecordActions, ReasonDialog } from "./AdminRecordControls";
import { useIdempotencyKey } from "./PolicyPanels";
import { Fact, NetworkHistoryModal, ProviderScope, networkApi, useDate, useLoad } from "./NetworkAdminShared";

/**
 * Phase 19.9 — Contracts & coverage (design 58).
 *
 * ============================================================================================================
 * WHAT THIS SECTION WAS
 * ============================================================================================================
 * A five-column read-only table. A provider's contracts could be looked at and nothing else: no way to raise
 * one, correct a date on one, price a service under one, activate one, or end one. Every one of those
 * endpoints existed or was one PUT away, and the screen offered none of them — so the actual workflow was a
 * developer with psql, or a contract that stayed Draft forever and priced nothing.
 *
 * ============================================================================================================
 * A CONTRACT IN FORCE IS NOT AN EDITABLE RECORD
 * ============================================================================================================
 * A Draft contract has priced nothing, so all of it is still an edit. Once it is Active its window is what
 * claims were settled against, and the screen says so rather than letting the server refuse after the fact:
 * the number, the start date and the price of an existing line are read-only, and the way to change a tariff
 * is a new contract. New CODES may still be added to a live contract — a service that was not on the list
 * cannot have been priced under it, so nothing already adjudicated can move.
 *
 * ============================================================================================================
 * TERMINATION REPORTS WHAT IT DID
 * ============================================================================================================
 * Ending a provider's last contract in force leaves them Active in the directory and routable for nothing.
 * The server answers with that fact rather than refusing (design 57's asymmetry: ending a contract IS the
 * operation), and this renders it as a sentence instead of a silent pair of disagreeing truths.
 */

const S = {
  title: { en: "Contracts & coverage", ar: "العقود والتغطية" },
  subtitle: {
    en: "What Mersal has agreed with each provider, for how long, and at what price.",
    ar: "ما اتفقت عليه مرسال مع كل مقدم خدمة، ولأي مدة، وبأي سعر.",
  },
  contractNo: { en: "Contract", ar: "العقد" },
  status: { en: "Status", ar: "الحالة" },
  from: { en: "From", ar: "من" },
  to: { en: "Until", ar: "إلى" },
  lines: { en: "Priced services", ar: "الخدمات المسعّرة" },
  openEnded: { en: "Open-ended", ar: "مفتوح" },
  inEffect: { en: "In effect today", ar: "ساري اليوم" },
  noContracts: { en: "No contracts recorded for this provider.", ar: "لا توجد عقود مسجلة لمقدم الخدمة." },
  newContract: { en: "New contract", ar: "عقد جديد" },
  editContract: { en: "Edit contract", ar: "تعديل العقد" },
  activate: { en: "Activate", ar: "تفعيل" },
  terminate: { en: "Terminate", ar: "إنهاء" },
  history: { en: "Contract history", ar: "سجل العقد" },
  // ── the form ────────────────────────────────────────────────────────────────────────────────────────────
  numberLabel: { en: "Contract number", ar: "رقم العقد" },
  numberHint: {
    en: "What invoices and claims cite. It cannot be changed once the contract is in force.",
    ar: "ما تستشهد به الفواتير والمطالبات. لا يمكن تغييره بعد سريان العقد.",
  },
  fromLabel: { en: "In force from", ar: "ساري من" },
  toLabel: { en: "Until (leave empty for open-ended)", ar: "حتى (اتركه فارغًا للعقد المفتوح)" },
  toHint: {
    en: "Two contracts with the same provider may not overlap: two prices for the same service on the same day, and nothing to choose between them.",
    ar: "لا يجوز تداخل عقدين مع مقدم الخدمة نفسه: سعران لنفس الخدمة في اليوم نفسه، دون ما يرجّح بينهما.",
  },
  startFixed: {
    en: "This contract is in force. Its number and start date are what claims have been priced against and are no longer editable.",
    ar: "هذا العقد ساري. رقمه وتاريخ بدايته هما ما سُعّرت عليه المطالبات ولم يعد بالإمكان تعديلهما.",
  },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  close: { en: "Close", ar: "إغلاق" },
  needNumber: { en: "A contract number is required.", ar: "رقم العقد مطلوب." },
  needFrom: { en: "A start date is required.", ar: "تاريخ البداية مطلوب." },
  // ── activation ──────────────────────────────────────────────────────────────────────────────────────────
  activateTitle: { en: "Activate this contract?", ar: "تفعيل هذا العقد؟" },
  activateBody: {
    en: "From now on, claims for the services priced here are settled at these prices. A contract with no priced services cannot be activated.",
    ar: "من الآن، تُسوّى مطالبات الخدمات المسعّرة هنا بهذه الأسعار. لا يمكن تفعيل عقد بلا خدمات مسعّرة.",
  },
  // ── termination ─────────────────────────────────────────────────────────────────────────────────────────
  terminateTitle: { en: "Terminate {0}?", ar: "إنهاء {0}؟" },
  terminateBody: {
    en: "The contract stops pricing anything from now on. It is kept, with the reason, so a claim settled under it can still be explained.",
    ar: "يتوقف العقد عن تسعير أي شيء من الآن. ويُحفَظ مع السبب حتى يظل بالإمكان تفسير مطالبة سُوِّيت بموجبه.",
  },
  terminateLastWarning: {
    en: "This is the only contract in effect for this provider. Ending it leaves them Active in the directory and routable for nothing — no order or referral will reach them until another contract is in force.",
    ar: "هذا هو العقد الساري الوحيد لمقدم الخدمة. إنهاؤه يتركه «نشطًا» في الدليل ولا يمكن توجيه أي خدمة إليه — لن يصله طلب أو إحالة حتى يسري عقد آخر.",
  },
  becameUnroutable: {
    en: "{0} is now Active in the directory with no contract in effect. Nothing will be routed to them until one is.",
    ar: "{0} الآن «نشط» في الدليل بلا عقد ساري. لن يُوجَّه إليه شيء حتى يسري عقد.",
  },
  // ── service lines ───────────────────────────────────────────────────────────────────────────────────────
  linesTitle: { en: "Priced services", ar: "الخدمات المسعّرة" },
  linesHint: {
    en: "What this contract pays for, and at what price. A contract with none cannot be activated.",
    ar: "ما يغطيه هذا العقد وبأي سعر. لا يمكن تفعيل عقد بلا خدمات.",
  },
  noLines: { en: "No services priced under this contract yet.", ar: "لا توجد خدمات مسعّرة بموجب هذا العقد بعد." },
  serviceType: { en: "Service", ar: "الخدمة" },
  codeSystem: { en: "Code system", ar: "نظام الترميز" },
  code: { en: "Code", ar: "الرمز" },
  price: { en: "Agreed price", ar: "السعر المتفق عليه" },
  priceWithheld: { en: "Restricted for your role", ar: "مقيّد حسب دورك" },
  addLine: { en: "Add a priced service", ar: "إضافة خدمة مسعّرة" },
  editPrice: { en: "Change price", ar: "تعديل السعر" },
  removeLine: { en: "Remove", ar: "إزالة" },
  removeTitle: { en: "Remove {0} from this contract?", ar: "إزالة {0} من هذا العقد؟" },
  removeBody: {
    en: "The service stops being covered by this contract. Only possible while the contract is a draft — once it is in force, a price is what claims settled at.",
    ar: "تتوقف تغطية الخدمة بموجب هذا العقد. متاح فقط ما دام العقد مسودة — بعد سريانه يصبح السعر ما سُوّيت به المطالبات.",
  },
  currency: { en: "Currency", ar: "العملة" },
  liveContractNote: {
    en: "This contract is in force. New codes may be added — a service that was not on the list cannot have been priced under it — but an existing price is superseded by a new contract, not edited here.",
    ar: "هذا العقد ساري. يمكن إضافة رموز جديدة — فالخدمة التي لم تكن مدرجة لم تُسعَّر بموجبه — أما السعر القائم فيُستبدَل بعقد جديد لا بالتعديل هنا.",
  },
  closedContractNote: {
    en: "This contract is closed. Add the code to the contract that is in force, or raise a new one.",
    ar: "هذا العقد منتهٍ. أضف الرمز إلى العقد الساري أو أنشئ عقدًا جديدًا.",
  },
  selectContract: { en: "Select a contract to see and price its services.", ar: "اختر عقدًا لعرض خدماته وتسعيرها." },
  searchLines: { en: "Search priced services", ar: "بحث في الخدمات المسعّرة" },
  searchLinesHint: { en: "Code or service type", ar: "الرمز أو نوع الخدمة" },
  noLineMatches: {
    en: "No priced service matches. Change the search or clear it.",
    ar: "لا توجد خدمة مسعّرة مطابقة. عدّل البحث أو امسحه.",
  },
  readOnly: {
    en: "Contract terms are administered by Mersal's Network Team. You are seeing your own record.",
    ar: "تدير شروط العقود إدارة الشبكة في مرسال. أنت ترى سجلك الخاص.",
  },
} satisfies Record<string, Localized>;

/** The vocabularies the server accepts, quoted from its enums. `Radiology` is the successor spelling of
 *  `Imaging` and both are offered for the length of the expand/contract window (design 45 §1). */
const SERVICE_TYPES = ["Lab", "Radiology", "Imaging", "Consult", "Procedure"] as const;
const CODE_SYSTEMS = ["CPT", "LOINC", "LOCAL"] as const;

const HISTORY_LABELS: Record<string, Localized> = {
  contract_no: S.contractNo,
  status: S.status,
  effective_from: S.from,
  effective_to: S.to,
};

export function NetworkContracts() {
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
      <AsyncSection state={providers} isEmpty={() => false} emptyLabel={S.noContracts}>
        {(rows) => (
          <ProviderScope providers={rows} picked={picked} onPick={setPicked} title={t(S.title)}>
            {(p) => <ContractsPanel provider={p} mayWrite={mayWrite} />}
          </ProviderScope>
        )}
      </AsyncSection>
    </div>
  );
}

function ContractsPanel({ provider, mayWrite }: { provider: ProviderSummary; mayWrite: boolean }) {
  const t = useLoc();
  const date = useDate();
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  const [form, setForm] = useState<{ mode: "create" } | { mode: "edit"; contract: ContractAdmin } | null>(null);
  const [activating, setActivating] = useState<ContractAdmin | null>(null);
  const [terminating, setTerminating] = useState<ContractAdmin | null>(null);
  const [historyFor, setHistoryFor] = useState<ContractAdmin | null>(null);

  const load = useCallback(() => networkApi.contracts(provider.id), [provider.id]);
  const [contracts, reload] = useLoad(load, [provider.id]);

  const inEffectCount = useMemo(
    () => (contracts ?? []).filter((c) => c.inEffect).length,
    [contracts],
  );

  const columns: Column<ContractAdmin>[] = [
    {
      key: "no", header: t(S.contractNo), cell: (c) => <span className="tnum">{c.contractNo}</span>,
      sortable: true, sortValue: (c) => c.contractNo,
    },
    {
      key: "status", header: t(S.status),
      cell: (c) => (
        <div className="pay-chips">
          <StatusChip kind={statusKind(c.status)} label={c.status} />
          {c.inEffect && <StatusChip kind="ok" label={t(S.inEffect)} />}
        </div>
      ),
      sortable: true, sortValue: (c) => c.status,
    },
    { key: "from", header: t(S.from), cell: (c) => <span className="tnum">{date(c.effectiveFrom)}</span>, sortable: true, sortValue: (c) => c.effectiveFrom },
    {
      key: "to", header: t(S.to),
      cell: (c) => <span className={c.effectiveTo ? "tnum" : "pol-muted"}>{c.effectiveTo ? date(c.effectiveTo) : t(S.openEnded)}</span>,
    },
    { key: "lines", header: t(S.lines), cell: (c) => c.serviceLines, numeric: true, sortable: true, sortValue: (c) => c.serviceLines },
    {
      key: "actions", header: "", cell: (c) => (
        <RecordActions
          onHistory={() => setHistoryFor(c)}
          onEdit={mayWrite && c.status !== "Terminated" ? () => setForm({ mode: "edit", contract: c }) : undefined}
          editLabel={S.editContract}
          status={
            mayWrite && c.status === "Draft"
              ? { label: S.activate, icon: "check2", onClick: () => setActivating(c) }
              : mayWrite && c.status !== "Terminated"
                ? { label: S.terminate, icon: "cross", onClick: () => setTerminating(c) }
                : undefined
          }
        />
      ),
    },
  ];

  const current = (contracts ?? []).find((c) => c.contractId === selected) ?? null;

  return (
    <div className="pol-stack">
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {mayWrite && (
        <div className="screen-toolbar">
          <span />
          <Button variant="primary" leadingIcon={<Icon name="plus" />} onClick={() => setForm({ mode: "create" })}>
            {t(S.newContract)}
          </Button>
        </div>
      )}

      <Card as="section">
        <DataTable
          columns={columns}
          rows={contracts ?? []}
          rowKey={(c) => c.contractId}
          caption={t(S.title)}
          emptyLabel={t(S.noContracts)}
          interactive
          selectedKey={selected}
          onSelect={(c) => setSelected(c.contractId)}
        />
      </Card>

      {!selected && (contracts ?? []).length > 0 && <InlineAlert tone="info">{t(S.selectContract)}</InlineAlert>}

      {current && (
        <ServiceLinesPanel
          contract={current}
          mayWrite={mayWrite}
          onChanged={async () => { reload(); }}
          onError={setError}
        />
      )}

      {form && (
        <ContractForm
          providerId={provider.id}
          contract={form.mode === "edit" ? form.contract : null}
          onClose={() => setForm(null)}
          onSaved={() => { setForm(null); reload(); setAnnounce(t(S.title)); }}
        />
      )}

      {activating && (
        <ReasonDialog
          title={S.activateTitle}
          body={S.activateBody}
          confirmLabel={S.activate}
          onConfirm={async (_reason, key) => { await networkApi.activateContract(activating.contractId, key); }}
          onClose={() => setActivating(null)}
          onDone={() => { setActivating(null); reload(); }}
        />
      )}

      {terminating && (
        <TerminateContractDialog
          contract={terminating}
          lastInEffect={terminating.inEffect && inEffectCount === 1}
          providerName={provider.legalName}
          onClose={() => setTerminating(null)}
          onDone={(msg) => { setTerminating(null); reload(); if (msg) setError(msg); }}
        />
      )}

      {historyFor && (
        <NetworkHistoryModal
          title={S.history}
          labels={HISTORY_LABELS}
          load={() => networkApi.contractHistory(historyFor.contractId)}
          onClose={() => setHistoryFor(null)}
        />
      )}
    </div>
  );
}

function statusKind(status: string): "ok" | "warn" | "neu" | "info" {
  if (status === "Active") return "ok";
  if (status === "Draft") return "info";
  if (status === "Terminated") return "warn";
  return "neu";
}

// ── The contract form ───────────────────────────────────────────────────────────────────────────────────

function ContractForm({
  providerId, contract, onClose, onSaved,
}: {
  providerId: string;
  contract: ContractAdmin | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [contractNo, setContractNo] = useState(contract?.contractNo ?? "");
  const [from, setFrom] = useState(contract?.effectiveFrom ?? "");
  const [to, setTo] = useState(contract?.effectiveTo ?? "");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  // In force ⇒ the number and the start are history, and the form says so rather than letting the server
  // refuse a change the operator has already typed.
  const locked = Boolean(contract) && contract!.status !== "Draft";

  async function submit() {
    if (!contractNo.trim()) { setProblem(S.needNumber); return; }
    if (!from) { setProblem(S.needFrom); return; }
    setBusy(true);
    setProblem(null);
    const body: ContractWrite = {
      contractNo: contractNo.trim(),
      effectiveFrom: from,
      effectiveTo: to ? to : null,
    };
    try {
      if (contract) await networkApi.updateContract(contract.contractId, body);
      else await networkApi.createContract(providerId, body, key);
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
      title={t(contract ? S.editContract : S.newContract)}
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
        {locked && <InlineAlert tone="info">{t(S.startFixed)}</InlineAlert>}
        <InputField
          label={t(S.numberLabel)}
          help={t(S.numberHint)}
          value={contractNo}
          onChange={(e) => setContractNo(e.currentTarget.value)}
          disabled={locked}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.fromLabel)}
          type="date"
          value={from}
          onChange={(e) => setFrom(e.currentTarget.value)}
          disabled={locked}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.toLabel)}
          help={t(S.toHint)}
          type="date"
          value={to}
          onChange={(e) => setTo(e.currentTarget.value)}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
      </div>
    </Modal>
  );
}

// ── Termination ─────────────────────────────────────────────────────────────────────────────────────────

function TerminateContractDialog({
  contract, lastInEffect, providerName, onClose, onDone,
}: {
  contract: ContractAdmin;
  lastInEffect: boolean;
  providerName: string;
  onClose: () => void;
  onDone: (message: Localized | null) => void;
}) {
  const [result, setResult] = useState<Localized | null>(null);
  return (
    <ReasonDialog
      title={fillLocalized(S.terminateTitle, contract.contractNo)}
      body={S.terminateBody}
      confirmLabel={S.terminate}
      onConfirm={async (reason, key) => {
        const r = await networkApi.terminateContract(contract.contractId, reason, key);
        // The server's own answer, not a guess made before the write: it recomputes routability from the
        // rows the router will read, after the termination has committed.
        setResult(r.providerBecomesUnroutable ? fillLocalized(S.becameUnroutable, providerName) : null);
      }}
      onClose={onClose}
      onDone={() => onDone(result)}
    >
      {/* Said BEFORE the act, not reported after it. The consequence is the reason somebody might not do it. */}
      {lastInEffect && <LastContractWarning />}
    </ReasonDialog>
  );
}

function LastContractWarning() {
  const t = useLoc();
  return <InlineAlert tone="warn">{t(S.terminateLastWarning)}</InlineAlert>;
}

// ── Priced services ─────────────────────────────────────────────────────────────────────────────────────

function ServiceLinesPanel({
  contract, mayWrite, onChanged, onError,
}: {
  contract: ContractAdmin;
  mayWrite: boolean;
  onChanged: () => void;
  onError: (l: Localized | null) => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [adding, setAdding] = useState(false);
  const [repricing, setRepricing] = useState<ServiceLine | null>(null);
  const [removing, setRemoving] = useState<ServiceLine | null>(null);

  const load = useCallback(() => networkApi.serviceLines(contract.contractId), [contract.contractId]);
  const [lines, reload] = useLoad(load, [contract.contractId]);

  const draft = contract.status === "Draft";
  const closed = contract.status === "Expired" || contract.status === "Terminated";

  const columns: Column<ServiceLine>[] = [
    { key: "type", header: t(S.serviceType), cell: (l) => l.serviceType, sortable: true, sortValue: (l) => l.serviceType },
    { key: "system", header: t(S.codeSystem), cell: (l) => l.codeSystem, sortable: true, sortValue: (l) => l.codeSystem },
    { key: "code", header: t(S.code), cell: (l) => <span className="tnum">{l.code}</span>, sortable: true, sortValue: (l) => l.code },
    {
      key: "price", header: t(S.price), numeric: true,
      // Withheld as the WHOLE field, never as a zero: "you are not being shown this" and "this is free" are
      // different claims, and only one of them is true.
      cell: (l) => l.agreedPrice === null || l.agreedPrice === undefined
        ? <span className="pol-muted">{t(S.priceWithheld)}</span>
        : <span className="tnum">{fmt.money(l.agreedPrice)}</span>,
    },
    {
      key: "actions", header: "",
      cell: (l) => (mayWrite && draft ? (
        <div className="rst-actions">
          <Button variant="ghost" size="sm" aria-label={t(S.editPrice)} title={t(S.editPrice)} onClick={() => setRepricing(l)}>
            <Icon name="pen" />
          </Button>
          <Button variant="ghost" size="sm" aria-label={t(S.removeLine)} title={t(S.removeLine)} onClick={() => setRemoving(l)}>
            <Icon name="cross" />
          </Button>
        </div>
      ) : null),
    },
  ];

  const query = useTableQuery<ServiceLine>({
    rows: lines ?? [],
    columns,
    // The CODE is what an operator arrives holding — off a claim, a price list or a phone call — so it is
    // searchable alongside the service type it belongs to.
    searchText: (l) => `${l.code} ${l.codeSystem} ${l.serviceType}`,
    searchLabel: t(S.searchLines),
    searchPlaceholder: t(S.searchLinesHint),
    pageSize: 25,
    initialSortKey: "code",
    persistKey: "network-service-lines",
  });

  return (
    <Card as="section">
      <div className="pol-stack">
        <div className="screen-toolbar">
          <div className="pay-head">
            <h3 style={{ margin: 0 }}>{t(S.linesTitle)}</h3>
            <p className="pol-muted" style={{ margin: 0 }}>{t(S.linesHint)}</p>
          </div>
          {mayWrite && !closed && (
            <Button variant="secondary" leadingIcon={<Icon name="plus" />} onClick={() => setAdding(true)}>
              {t(S.addLine)}
            </Button>
          )}
        </div>

        {!draft && !closed && <InlineAlert tone="info">{t(S.liveContractNote)}</InlineAlert>}
        {closed && <InlineAlert tone="info">{t(S.closedContractNote)}</InlineAlert>}

        {/* A hospital's tariff runs to hundreds of codes, and finding one of them by scrolling is the exact
            defect `DataTableView` exists for. The contracts table above it is a bare table on purpose: it is
            one provider's contracts, which is a handful. */}
        <DataTableView
          query={query}
          columns={columns}
          rowKey={(l) => l.serviceLineId}
          caption={t(S.linesTitle)}
          emptyLabel={t(S.noLines)}
          noMatchesLabel={t(S.noLineMatches)}
        />
      </div>

      {adding && (
        <ServiceLineForm
          contractId={contract.contractId}
          onClose={() => setAdding(false)}
          onSaved={() => { setAdding(false); reload(); onChanged(); }}
          onError={onError}
        />
      )}

      {repricing && (
        <RepriceDialog
          contractId={contract.contractId}
          line={repricing}
          onClose={() => setRepricing(null)}
          onSaved={() => { setRepricing(null); reload(); }}
        />
      )}

      {removing && (
        <ReasonDialog
          title={fillLocalized(S.removeTitle, removing.code)}
          body={S.removeBody}
          confirmLabel={S.removeLine}
          onConfirm={async () => { await networkApi.removeServiceLine(contract.contractId, removing.serviceLineId); }}
          onClose={() => setRemoving(null)}
          onDone={() => { setRemoving(null); reload(); onChanged(); }}
        />
      )}
    </Card>
  );
}

function ServiceLineForm({
  contractId, onClose, onSaved, onError,
}: {
  contractId: string;
  onClose: () => void;
  onSaved: () => void;
  onError: (l: Localized | null) => void;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [serviceType, setServiceType] = useState<string | null>(null);
  const [codeSystem, setCodeSystem] = useState<string | null>("CPT");
  const [code, setCode] = useState("");
  const [price, setPrice] = useState("");
  const [currency, setCurrency] = useState("EGP");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  const priceValue = price.trim() === "" ? null : Number(price);
  const complete = Boolean(serviceType && codeSystem && code.trim() && priceValue !== null && !Number.isNaN(priceValue) && priceValue >= 0);

  async function submit() {
    if (!complete) return;
    setBusy(true);
    setProblem(null);
    try {
      await networkApi.addServiceLine(contractId, {
        serviceType: serviceType!,
        codeSystem: codeSystem!,
        code: code.trim(),
        agreedPrice: priceValue!,
        currencyCode: currency.trim().toUpperCase() || "EGP",
      }, key);
      onError(null);
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
      title={t(S.addLine)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={busy} disabled={!complete} onClick={() => void submit()}>{t(S.save)}</Button>
        </>
      }
    >
      <div className="pol-stack">
        {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
        <ComboboxField
          label={t(S.serviceType)}
          options={SERVICE_TYPES.map((v) => ({ value: v, label: v }))}
          value={serviceType}
          onChange={setServiceType}
          required
        />
        <ComboboxField
          label={t(S.codeSystem)}
          options={CODE_SYSTEMS.map((v) => ({ value: v, label: v }))}
          value={codeSystem}
          onChange={setCodeSystem}
          required
        />
        <InputField
          label={t(S.code)}
          value={code}
          onChange={(e) => setCode(e.currentTarget.value)}
          autoComplete="off"
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.price)}
          type="number"
          inputMode="decimal"
          min={0}
          step="0.01"
          value={price}
          onChange={(e) => setPrice(e.currentTarget.value)}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
        <InputField
          label={t(S.currency)}
          value={currency}
          onChange={(e) => setCurrency(e.currentTarget.value)}
          maxLength={3}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
      </div>
    </Modal>
  );
}

function RepriceDialog({
  contractId, line, onClose, onSaved,
}: {
  contractId: string;
  line: ServiceLine;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const [price, setPrice] = useState(line.agreedPrice === null || line.agreedPrice === undefined ? "" : String(line.agreedPrice));
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Localized | null>(null);

  const value = price.trim() === "" ? null : Number(price);
  const valid = value !== null && !Number.isNaN(value) && value >= 0;

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(S.editPrice)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button
            variant="primary"
            leadingIcon={<Icon name="check2" />}
            loading={busy}
            disabled={!valid}
            onClick={async () => {
              setBusy(true);
              setProblem(null);
              try {
                await networkApi.updateServiceLine(contractId, line.serviceLineId, {
                  agreedPrice: value!,
                  currencyCode: line.currencyCode ?? undefined,
                });
                onSaved();
              } catch (e) {
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
        <dl className="pol-identity-list">
          <Fact label={t(S.code)} value={`${line.codeSystem} ${line.code}`} mono />
          <Fact label={t(S.serviceType)} value={line.serviceType} />
        </dl>
        <InputField
          label={t(S.price)}
          type="number"
          inputMode="decimal"
          min={0}
          step="0.01"
          value={price}
          onChange={(e) => setPrice(e.currentTarget.value)}
          style={{ maxInlineSize: "var(--field-max)" }}
        />
      </div>
    </Modal>
  );
}
