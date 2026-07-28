import { useState } from "react";
import { Button, Card, DataTable, InlineAlert, InputField, Modal, StatusChip, useToast } from "@mersal/design-system";
import { useWrite } from "../api/useWrite";
import type { Column } from "@mersal/design-system";
import type { BeneficiaryRow, Localized, RegisterBeneficiaryInput, RegistrationWorkItem } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useAuth } from "../auth/AuthProvider";
import { ApiError } from "../api/http";
import { AsyncSection, classifyReadError, PageHeader, useLoc } from "./_shared";

const S = {
  manageTitle: { en: "Search / manage", ar: "بحث / إدارة" },
  statusTitle: { en: "Status & reactivation", ar: "الحالة وإعادة التفعيل" },
  registerTitle: { en: "Register new", ar: "تسجيل جديد" },
  searchField: { en: "Search by name", ar: "ابحث بالاسم" },
  search: { en: "Search", ar: "بحث" },
  idle: { en: "Search for a beneficiary by name.", ar: "ابحث عن مستفيد بالاسم." },
  none: { en: "No beneficiaries match that search.", ar: "لا يوجد مستفيدون مطابقون." },
  retry: { en: "Retry", ar: "إعادة المحاولة" },
  name: { en: "Name", ar: "الاسم" },
  memberNo: { en: "Member no.", ar: "رقم العضوية" },
  identifier: { en: "Identifier", ar: "المعرّف" },
  status: { en: "Status", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  changeStatus: { en: "Change status", ar: "تغيير الحالة" },
  changeStatusFor: { en: "Change status", ar: "تغيير الحالة" },
  newStatus: { en: "New status", ar: "الحالة الجديدة" },
  reason: { en: "Reason", ar: "السبب" },
  reasonRequired: { en: "A reason is required for this change — it is recorded and reviewed.", ar: "السبب مطلوب لهذا التغيير — يُسجَّل ويُراجَع." },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  changed: { en: "Status updated.", ar: "تم تحديث الحالة." },
  // Blocked is the fraud state: the desk can neither place nor lift it (23 §1 — director's case review).
  blockedLocked: {
    en: "Blocked records are changed only by a Medical Director after case review.",
    ar: "السجلات المحظورة لا يغيّرها إلا المدير الطبي بعد مراجعة الحالة.",
  },
  // Action labels named for the OPERATION, not the target state — "Reinstate" tells the operator what they
  // are doing to the person; "Active" only tells them a database value.
  activate: { en: "Activate", ar: "تفعيل" },
  suspend: { en: "Suspend", ar: "إيقاف" },
  reinstate: { en: "Reinstate", ar: "إعادة تفعيل" },
  renew: { en: "Renew", ar: "تجديد" },
  reactivate: { en: "Reactivate", ar: "إعادة تنشيط" },
  deactivate: { en: "Deactivate", ar: "إلغاء التفعيل" },

  givenName: { en: "Given name", ar: "الاسم الأول" },
  familyName: { en: "Family name", ar: "اسم العائلة" },
  birthDate: { en: "Birth date", ar: "تاريخ الميلاد" },
  birthDateHelp: { en: "YYYY-MM-DD — optional", ar: "سنة-شهر-يوم — اختياري" },
  birthDateInvalid: { en: "Enter a real date as YYYY-MM-DD, not in the future.", ar: "أدخل تاريخًا صحيحًا بصيغة سنة-شهر-يوم، وليس في المستقبل." },
  idType: { en: "Identifier type", ar: "نوع المعرّف" },
  idValue: { en: "Identifier value", ar: "قيمة المعرّف" },
  phone: { en: "Phone", ar: "الهاتف" },
  register: { en: "Register beneficiary", ar: "تسجيل المستفيد" },
  registered: { en: "Beneficiary registered (Pending) — proceed to eligibility.", ar: "تم التسجيل (قيد الانتظار)." },
  required: { en: "Required.", ar: "مطلوب." },
  // The one 409 with a happy path: the person exists. Reloading the form (the generic conflict guidance)
  // would lead the operator to re-type and re-submit — manufacturing the duplicate record the identifier
  // check exists to prevent. The remedy is the search screen.
  alreadyRegistered: {
    en: "This identifier is already registered. Open Search / manage to find the existing record — registering again would create a duplicate.",
    ar: "هذا المعرّف مسجَّل بالفعل. افتح «بحث / إدارة» للعثور على السجل الموجود — التسجيل مرة أخرى سينشئ سجلًا مكررًا.",
  },
} satisfies Record<string, Localized>;

const ID_TYPES = ["NationalID", "Passport", "RefugeeID", "UNHCRNo"] as const;

const ID_TYPE_LABELS: Record<(typeof ID_TYPES)[number], Localized> = {
  NationalID: { en: "National ID", ar: "الرقم القومي" },
  Passport: { en: "Passport", ar: "جواز سفر" },
  RefugeeID: { en: "Refugee ID", ar: "بطاقة لاجئ" },
  UNHCRNo: { en: "UNHCR number", ar: "رقم المفوضية (UNHCR)" },
};

/**
 * The transitions this DESK may offer, per current status — the UI mirror of `BeneficiaryLifecycle` +
 * 23 §1's Actor column. Offering an illegal move (the old screen showed Activate/Suspend on every row)
 * just manufactures 409s: the server refuses, and the operator learns the rules by being told off.
 *
 * `needsReason` mirrors `RequiresReason`: a reason is demanded exactly where the server records one, and
 * not where it would be theatre (activation needs no justification — it is the default good outcome).
 * Blocked is absent on purpose: both edges of the fraud state are a director's, and the screen says so
 * instead of rendering a button that 403s.
 */
const DESK_TRANSITIONS: Record<string, Array<{ to: string; label: Localized; needsReason: boolean; danger?: boolean }>> = {
  Pending: [
    { to: "Active", label: S.activate, needsReason: false },
    { to: "Inactive", label: S.deactivate, needsReason: true, danger: true },
  ],
  Active: [
    { to: "Suspended", label: S.suspend, needsReason: true, danger: true },
    { to: "Inactive", label: S.deactivate, needsReason: true, danger: true },
  ],
  Suspended: [{ to: "Active", label: S.reinstate, needsReason: false }],
  Expired: [{ to: "Active", label: S.renew, needsReason: false }],
  Inactive: [{ to: "Active", label: S.reactivate, needsReason: false }],
  Blocked: [],
};

function beneficiaryColumns(t: (l: Localized) => string): Column<BeneficiaryRow>[] {
  return [
    { key: "name", header: t(S.name), cell: (r) => `${r.givenName} ${r.familyName}` },
    { key: "member", header: t(S.memberNo), cell: (r) => <span className="tnum">{r.memberNo ?? "—"}</span> },
    { key: "id", header: t(S.identifier), cell: (r) => <span className="tnum">{r.identifiers[0] ? `${r.identifiers[0].type}: ${r.identifiers[0].value}` : "—"}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
}

/** A shared name search that renders its results through a caller-supplied column set. */
function BeneficiarySearch({ title, extraCols }: { title: Localized; extraCols?: (reload: () => void) => Column<BeneficiaryRow> }) {
  const api = useApi();
  const t = useLoc();
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<"idle" | "loading" | "error" | "ready">("idle");
  const [error, setError] = useState<ApiError | null>(null);
  const [rows, setRows] = useState<BeneficiaryRow[]>([]);

  async function run(e: React.FormEvent) {
    e.preventDefault();
    if (query.trim().length < 1) return;
    setStatus("loading");
    setError(null);
    try {
      setRows(await api.beneficiarySearch({ name: query.trim() }));
      setStatus("ready");
    } catch (err) {
      // The typed failure is kept: a 403 ("outside your permissions") and a dropped connection demand
      // different actions, and the old single "couldn't reach the registry" line hid which one happened.
      setError(err instanceof ApiError ? err : new ApiError("network", String(err)));
      setStatus("error");
    }
  }
  const reload = () => void run({ preventDefault() {} } as React.FormEvent);

  const cols = beneficiaryColumns(t);
  if (extraCols) cols.push(extraCols(reload));

  return (
    <>
      <PageHeader title={t(title)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={run} className="stack" aria-label={t(title)}>
          <InputField label={t(S.searchField)} value={query} onChange={(e) => setQuery(e.currentTarget.value)} autoComplete="off" />
          <div><Button type="submit" variant="primary" loading={status === "loading"}>{t(S.search)}</Button></div>
        </form>
      </Card>
      <div aria-live="polite" style={{ marginTop: "var(--sp4)" }}>
        {status === "idle" && <Card style={{ padding: "var(--sp5)" }}><p className="muted">{t(S.idle)}</p></Card>}
        {status === "error" && (
          <Card style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            <InlineAlert tone="bad">
              <span>{t(classifyReadError(error).headline)}</span>
              {error?.problem?.detail ? (
                <span style={{ display: "block", marginTop: "var(--sp1)", opacity: 0.85, fontSize: "0.9em" }}>{error.problem.detail}</span>
              ) : null}
            </InlineAlert>
            {classifyReadError(error).remedy === "retry" ? (
              <div><Button variant="secondary" onClick={reload}>{t(S.retry)}</Button></div>
            ) : null}
          </Card>
        )}
        {status === "ready" && rows.length === 0 && <Card style={{ padding: "var(--sp5)" }}><StatusChip kind="neu" label={t(S.none)} /></Card>}
        {status === "ready" && rows.length > 0 && (
          <Card as="section" style={{ padding: "var(--sp3)" }}>
            <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(title)} />
          </Card>
        )}
      </div>
    </>
  );
}

/** Search / manage — find beneficiaries by name (read-only min-necessary identity view). */
export function BeneficiaryManage() {
  return <BeneficiarySearch title={S.manageTitle} />;
}

/** Status & reactivation — find a beneficiary, then apply a LEGAL lifecycle transition with a reason. */
export function BeneficiaryStatus() {
  const t = useLoc();
  const [target, setTarget] = useState<{ row: BeneficiaryRow; reload: () => void } | null>(null);

  const actionCol = (reload: () => void): Column<BeneficiaryRow> => ({
    key: "action",
    header: t(S.action),
    cell: (r) =>
      (DESK_TRANSITIONS[r.statusRaw] ?? []).length === 0 ? (
        // Not an empty cell: an absent button with no explanation reads as a broken screen. The desk can't
        // act on a Blocked record, and the reason why is the useful thing to say.
        <span className="muted">{t(S.blockedLocked)}</span>
      ) : (
        <Button variant="secondary" size="sm" onClick={() => setTarget({ row: r, reload })}>
          {t(S.changeStatus)}
        </Button>
      ),
  });

  return (
    <>
      <BeneficiarySearch title={S.statusTitle} extraCols={actionCol} />
      {target ? (
        <StatusChangeModal
          row={target.row}
          onClose={() => setTarget(null)}
          onChanged={() => {
            setTarget(null);
            // Server truth, not local patching: reactivation can ISSUE a member number now, and only a
            // re-query shows it. The re-read is a fresh disclosure and is audited as one.
            target.reload();
          }}
        />
      ) : null}
    </>
  );
}

function StatusChangeModal({ row, onClose, onChanged }: { row: BeneficiaryRow; onClose: () => void; onChanged: () => void }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const write = useWrite();
  const options = DESK_TRANSITIONS[row.statusRaw] ?? [];
  const [choice, setChoice] = useState(options.length === 1 ? options[0].to : "");
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);

  const selected = options.find((o) => o.to === choice);
  const reasonError = touched && selected?.needsReason && reason.trim() === "" ? t(S.reasonRequired) : undefined;

  const confirm = async () => {
    setTouched(true);
    if (!selected) return;
    if (selected.needsReason && reason.trim() === "") return;
    const ok = await write.run(() => api.changeBeneficiaryStatus(row.id, selected.to, reason.trim()));
    if (ok) {
      toast(t(S.changed), "ok");
      onChanged();
    }
    // On failure the modal STAYS OPEN with the typed error rendered below — the old screen's try/finally
    // swallowed the rejection entirely, so a 409 looked identical to success with a stopped spinner.
  };

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${t(S.changeStatusFor)} — ${row.givenName} ${row.familyName}`}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant={selected?.danger ? "danger" : "primary"} onClick={confirm} disabled={write.busy || !selected}>
            {t(S.confirm)}
          </Button>
        </>
      }
    >
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}

      <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
        <legend>{t(S.newStatus)}</legend>
        {options.map((o) => (
          <label key={o.to} style={{ display: "block", padding: "var(--sp1) 0" }}>
            <input type="radio" name="transition" value={o.to} checked={choice === o.to} onChange={() => setChoice(o.to)} />{" "}
            {t(o.label)}
          </label>
        ))}
      </fieldset>

      {selected?.needsReason ? (
        <InputField label={t(S.reason)} value={reason} error={reasonError} onChange={(e) => setReason(e.currentTarget.value)} autoComplete="off" />
      ) : null}
    </Modal>
  );
}

/** Register new — create a beneficiary (Pending), the first step of registration (US-001). */
export function BeneficiaryRegister() {
  const api = useApi();
  const t = useLoc();
  const [f, setF] = useState({ givenName: "", familyName: "", birthDate: "", idType: "NationalID", idValue: "", phone: "" });
  const [status, setStatus] = useState<"idle" | "saving" | "done">("idle");
  const [touched, setTouched] = useState(false);
  const write = useWrite();          // 18.D1 — per-form idempotency key + typed failures
  // The value is read BEFORE the functional updater: React nulls `currentTarget` once the handler returns,
  // and the updater can run after that (re-render rebasing) — the old screen carried this crash latently.
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const value = e.currentTarget.value;
    setF((s) => ({ ...s, [k]: value }));
  };

  // Per-field errors at the field, not one banner naming everything at once — the operator fixes what is
  // marked. Birth date is validated as a REAL calendar date: "2026-02-31" matches YYYY-MM-DD and the old
  // screen forwarded it to the server, whose 400 came back mapped to "reload the page".
  const errors = {
    givenName: touched && f.givenName.trim() === "" ? t(S.required) : undefined,
    familyName: touched && f.familyName.trim() === "" ? t(S.required) : undefined,
    idValue: touched && f.idValue.trim() === "" ? t(S.required) : undefined,
    birthDate: touched && f.birthDate.trim() !== "" && !isRealPastDate(f.birthDate.trim()) ? t(S.birthDateInvalid) : undefined,
  };
  const invalid = () =>
    f.givenName.trim() === "" || f.familyName.trim() === "" || f.idValue.trim() === "" ||
    (f.birthDate.trim() !== "" && !isRealPastDate(f.birthDate.trim()));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setTouched(true);
    if (invalid()) return;
    setStatus("saving");
    const input: RegisterBeneficiaryInput = {
      givenName: f.givenName.trim(),
      familyName: f.familyName.trim(),
      birthDate: f.birthDate.trim() || undefined,
      identifierType: f.idType as RegisterBeneficiaryInput["identifierType"],
      identifierValue: f.idValue.trim(),
      phone: f.phone.trim() || undefined,
    };
    // 18.D1 (U1): registering a beneficiary failed silently. The operator saw the spinner stop with the form
    // still full, retried, and created a second record for the same person — which then has to be merged.
    const ok = await write.run((key) => api.registerBeneficiary(input, key));
    if (ok) {
      setStatus("done");
      setTouched(false);
      // Clear ONLY on confirmed success: wiping a form after a failure destroys the operator's typing.
      setF({ givenName: "", familyName: "", birthDate: "", idType: "NationalID", idValue: "", phone: "" });
    } else {
      setStatus("idle");
    }
  }

  const isDuplicate = write.error?.problemType === "urn:hbmp:duplicate-identifier";

  return (
    <>
      <PageHeader title={t(S.registerTitle)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={submit} className="stack" aria-label={t(S.registerTitle)}>
          {/* NOT a <dl>: this reuses the kv-grid LAYOUT for form fields. A definition list here would be
              invalid (InputField renders no dt/dd) and would announce the form as a term/value list. */}
          <div className="kv-grid">
            <InputField label={t(S.givenName)} value={f.givenName} error={errors.givenName} onChange={set("givenName")} autoComplete="off" />
            <InputField label={t(S.familyName)} value={f.familyName} error={errors.familyName} onChange={set("familyName")} autoComplete="off" />
            <InputField label={t(S.birthDate)} help={t(S.birthDateHelp)} value={f.birthDate} error={errors.birthDate} onChange={set("birthDate")} inputMode="numeric" autoComplete="off" />
            <InputField label={t(S.phone)} value={f.phone} onChange={set("phone")} inputMode="tel" autoComplete="off" />
            {/* A closed vocabulary rendered as one — the old free-text field asked the operator to TYPE an
                enum member from a parenthetical hint, and "nationalid" (wrong case) was a validation error. */}
            <div className="mrs-field">
              <label className="mrs-label" htmlFor="reg-id-type">{t(S.idType)}</label>
              <select id="reg-id-type" className="mrs-control" value={f.idType} onChange={set("idType")}>
                {ID_TYPES.map((v) => <option key={v} value={v}>{t(ID_TYPE_LABELS[v])}</option>)}
              </select>
            </div>
            <InputField label={t(S.idValue)} value={f.idValue} error={errors.idValue} onChange={set("idValue")} autoComplete="off" />
          </div>
          <div aria-live="polite" className="stack" style={{ gap: "var(--sp2)" }}>
            {/* 18.D1 (U2): the server's own reason, translated and typed — a 409 reads differently from a
                dropped connection, because they demand opposite actions. The duplicate-identifier 409 gets
                its own copy: its remedy is the SEARCH screen, and the generic conflict guidance ("reload")
                walks the operator into creating the duplicate. */}
            {write.error && (
              <InlineAlert tone="bad">
                {isDuplicate ? t(S.alreadyRegistered) : t(write.error.message)}
              </InlineAlert>
            )}
            {status === "done" && <StatusChip kind="ok" label={t(S.registered)} />}
            <div><Button type="submit" variant="primary" loading={status === "saving"}>{t(S.register)}</Button></div>
          </div>
        </form>
      </Card>
    </>
  );
}

/** True for a syntactically valid YYYY-MM-DD naming a real calendar day that is not in the future. */
function isRealPastDate(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const [y, m, d] = value.split("-").map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  // Date.UTC rolls invalid days over (Feb 31 → Mar 3), so round-tripping detects them.
  if (date.getUTCFullYear() !== y || date.getUTCMonth() !== m - 1 || date.getUTCDate() !== d) return false;
  return date.getTime() <= Date.now();
}

// ================================================================ REGISTRATION APPROVALS (US-003)

const A = {
  title: { en: "Registration approvals", ar: "اعتماد التسجيلات" },
  intro: {
    en: "Pending registrations, oldest first. Approval needs verified documents and bound coverage; the decision itself is a supervisor's.",
    ar: "التسجيلات قيد الانتظار، الأقدم أولًا. يتطلب الاعتماد التحقق من المستندات وربط تغطية؛ والقرار نفسه من صلاحية المشرف.",
  },
  empty: { en: "No registrations waiting for review.", ar: "لا توجد تسجيلات بانتظار المراجعة." },
  person: { en: "Person", ar: "الشخص" },
  application: { en: "Application", ar: "الطلب" },
  docs: { en: "Documents verified", ar: "تم التحقق من المستندات" },
  coverage: { en: "Coverage bound", ar: "تم ربط التغطية" },
  notes: { en: "Notes", ar: "ملاحظات" },
  decide: { en: "Decide", ar: "قرار" },
  startReview: { en: "Start review", ar: "بدء المراجعة" },
  // Application-status chips (the beneficiary chip already says Pending — this is the WORKFLOW state).
  appPending: { en: "In review", ar: "قيد المراجعة" },
  appInfo: { en: "Info requested", ar: "بانتظار معلومات" },
  appRejected: { en: "Rejected", ar: "مرفوض" },
  notStarted: { en: "Not started", ar: "لم تبدأ" },

  decisionTitle: { en: "Registration decision", ar: "قرار التسجيل" },
  approve: { en: "Approve & activate", ar: "اعتماد وتفعيل" },
  requestInfo: { en: "Request information", ar: "طلب معلومات" },
  reject: { en: "Reject", ar: "رفض" },
  decisionLabel: { en: "Decision", ar: "القرار" },
  notesLabel: { en: "Notes", ar: "ملاحظات" },
  notesRequired: {
    en: "Notes are required — they go back to the officer (request info) or onto the record (reject).",
    ar: "الملاحظات مطلوبة — تعود إلى الموظف (طلب معلومات) أو تُسجَّل في الملف (رفض).",
  },
  approveBlocked: {
    en: "Approval needs both checks: documents verified and coverage bound.",
    ar: "يتطلب الاعتماد اكتمال الشرطين: التحقق من المستندات وربط التغطية.",
  },
  approved: { en: "Approved — member number", ar: "تم الاعتماد — رقم العضوية" },
  decided: { en: "Decision recorded.", ar: "تم تسجيل القرار." },
  supervisorOnly: {
    en: "Decisions are made by a beneficiary-management supervisor.",
    ar: "القرارات من صلاحية مشرف إدارة المستفيدين.",
  },
} satisfies Record<string, Localized>;

function appStatusChip(item: RegistrationWorkItem): { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: Localized } {
  if (!item.registration) return { kind: "neu", label: A.notStarted };
  switch (item.registration.status) {
    case "InfoRequested": return { kind: "warn", label: A.appInfo };
    case "Rejected": return { kind: "bad", label: A.appRejected };
    default: return { kind: "info", label: A.appPending };
  }
}

/**
 * The approver's worklist (US-003): verify the guards, then decide.
 *
 * Two roles share this screen with different halves. The OFFICER prepares — toggles the two guards as the
 * evidence arrives, and can open an application for a legacy record. The SUPERVISOR decides. The decision
 * buttons are hidden from the officer as a courtesy only (§6 — UI gating is cosmetic); the server refuses a
 * hand-crafted officer decision with `urn:hbmp:approver-required`, because the person who vouched for the
 * documents must not be the one who activates the member.
 */
export function RegistrationApprovals() {
  const api = useApi();
  const t = useLoc();
  const { session } = useAuth();
  const { toast } = useToast();
  const write = useWrite();
  const [reloadKey, setReloadKey] = useState(0);
  const [target, setTarget] = useState<RegistrationWorkItem | null>(null);
  const state = useAsync<RegistrationWorkItem[]>(() => api.registrationWorklist(), [reloadKey]);
  const reload = () => setReloadKey((k) => k + 1);
  const isSupervisor = session?.role === "beneficiary_mgmt_supervisor";

  const toggle = async (item: RegistrationWorkItem, key: "documentsVerified" | "coverageBound") => {
    if (!item.registration) return;
    const ok = await write.run(() => api.setRegistrationChecks(item.registration!.id, { [key]: !item.registration![key] }));
    if (ok) reload();
  };

  const start = async (item: RegistrationWorkItem) => {
    const ok = await write.run((key) => api.createRegistration(item.beneficiary.id, key));
    if (ok) reload();
  };

  const cols: Column<RegistrationWorkItem>[] = [
    {
      key: "person",
      header: t(A.person),
      cell: (r) => (
        <span>
          {r.beneficiary.givenName} {r.beneficiary.familyName}
          <span className="muted tnum" style={{ display: "block" }}>
            {r.beneficiary.identifiers[0] ? `${r.beneficiary.identifiers[0].type}: ${r.beneficiary.identifiers[0].value}` : "—"}
          </span>
        </span>
      ),
    },
    {
      key: "application",
      header: t(A.application),
      cell: (r) => {
        const chip = appStatusChip(r);
        return <StatusChip kind={chip.kind} label={t(chip.label)} />;
      },
    },
    {
      // The two approval guards as real checkboxes: the officer records evidence as it arrives, and the
      // supervisor sees at a glance what is still missing. Disabled (not hidden) when there is no
      // application yet — the state is legible either way.
      key: "checks",
      header: t(A.docs),
      cell: (r) => (
        <label style={{ display: "inline-flex", alignItems: "center", gap: "var(--sp2)" }}>
          <input
            type="checkbox"
            checked={r.registration?.documentsVerified ?? false}
            disabled={!r.registration || write.busy}
            onChange={() => void toggle(r, "documentsVerified")}
            aria-label={`${t(A.docs)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName}`}
          />
        </label>
      ),
    },
    {
      key: "coverage",
      header: t(A.coverage),
      cell: (r) => (
        <label style={{ display: "inline-flex", alignItems: "center", gap: "var(--sp2)" }}>
          <input
            type="checkbox"
            checked={r.registration?.coverageBound ?? false}
            disabled={!r.registration || write.busy}
            onChange={() => void toggle(r, "coverageBound")}
            aria-label={`${t(A.coverage)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName}`}
          />
        </label>
      ),
    },
    {
      // The approver's notes are ON the worklist, not behind a click: "UNHCR letter is expired" is the
      // officer's to-do item, and hiding it in a detail view is how it gets missed.
      key: "notes",
      header: t(A.notes),
      cell: (r) => <span className="muted">{r.registration?.notes ?? "—"}</span>,
    },
    {
      key: "action",
      header: "",
      cell: (r) =>
        !r.registration || r.registration.status === "Rejected" ? (
          <Button variant="secondary" size="sm" onClick={() => void start(r)}>{t(A.startReview)}</Button>
        ) : isSupervisor ? (
          <Button variant="primary" size="sm" onClick={() => setTarget(r)}>{t(A.decide)}</Button>
        ) : (
          <span className="muted">{t(A.supervisorOnly)}</span>
        ),
    },
  ];

  return (
    <>
      <PageHeader title={t(A.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted" style={{ marginTop: 0 }}>{t(A.intro)}</p>
        {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}
        <AsyncSection<RegistrationWorkItem[]> state={state} isEmpty={(d) => d.length === 0} emptyLabel={A.empty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.beneficiary.id} caption={t(A.title)} />}
        </AsyncSection>
      </Card>
      {target?.registration ? (
        <DecisionModal
          item={target}
          onClose={() => setTarget(null)}
          onDecided={(memberNo) => {
            setTarget(null);
            toast(memberNo ? `${t(A.approved)}: ${memberNo}` : t(A.decided), "ok");
            reload();
          }}
        />
      ) : null}
    </>
  );
}

function DecisionModal({ item, onClose, onDecided }: { item: RegistrationWorkItem; onClose: () => void; onDecided: (memberNo?: string) => void }) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const reg = item.registration!;
  const canApprove = reg.documentsVerified && reg.coverageBound;
  const [decision, setDecision] = useState<"Approve" | "RequestInfo" | "Reject" | "">(canApprove ? "Approve" : "");
  const [notes, setNotes] = useState("");
  const [touched, setTouched] = useState(false);

  const needsNotes = decision === "RequestInfo" || decision === "Reject";
  const notesError = touched && needsNotes && notes.trim() === "" ? t(A.notesRequired) : undefined;

  const confirm = async () => {
    setTouched(true);
    if (!decision) return;
    if (needsNotes && notes.trim() === "") return;
    // The issued member number is the ONE fact the approver must hand onward (it goes on the card), so it
    // is captured out of the write rather than re-queried — a re-query races the projection and can miss it.
    let memberNo: string | undefined;
    const ok = await write.run(async () => {
      const r = await api.decideRegistration(reg.id, decision, notes.trim() || undefined);
      memberNo = r.memberNo;
      return r;
    });
    if (ok) onDecided(memberNo);
  };

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${t(A.decisionTitle)} — ${item.beneficiary.givenName} ${item.beneficiary.familyName}`}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant={decision === "Reject" ? "danger" : "primary"} onClick={confirm} disabled={write.busy || !decision}>
            {t(S.confirm)}
          </Button>
        </>
      }
    >
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}

      <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
        <legend>{t(A.decisionLabel)}</legend>
        <label style={{ display: "block", padding: "var(--sp1) 0" }}>
          <input type="radio" name="decision" value="Approve" disabled={!canApprove} checked={decision === "Approve"} onChange={() => setDecision("Approve")} />{" "}
          {t(A.approve)}
        </label>
        {/* Disabled WITH the reason beside it (§6 — the server re-checks either way): an approve option
            that is simply missing reads as a broken screen, not an incomplete application. */}
        {!canApprove ? <InlineAlert tone="info">{t(A.approveBlocked)}</InlineAlert> : null}
        <label style={{ display: "block", padding: "var(--sp1) 0" }}>
          <input type="radio" name="decision" value="RequestInfo" checked={decision === "RequestInfo"} onChange={() => setDecision("RequestInfo")} />{" "}
          {t(A.requestInfo)}
        </label>
        <label style={{ display: "block", padding: "var(--sp1) 0" }}>
          <input type="radio" name="decision" value="Reject" checked={decision === "Reject"} onChange={() => setDecision("Reject")} />{" "}
          {t(A.reject)}
        </label>
      </fieldset>

      {needsNotes ? (
        <InputField label={t(A.notesLabel)} value={notes} error={notesError} onChange={(e) => setNotes(e.currentTarget.value)} autoComplete="off" />
      ) : null}
    </Modal>
  );
}
