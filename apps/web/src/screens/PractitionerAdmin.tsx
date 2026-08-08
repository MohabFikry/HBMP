import { useMemo, useState } from "react";
import {
  Button,
  Card,
  Combobox,
  DataTable,
  InlineAlert,
  InputField,
  StatusChip,
  useTheme,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  BranchSummary,
  CreatePractitionerInput,
  IdentityUser,
  Localized,
  Practitioner,
  PractitionerAttachFailure,
  Specialty,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Doctors & Clinicians", ar: "الأطباء والإكلينيكيون" },
  intro: {
    en: "A clinician's specialty and the clinics they work at are what the booking screen filters on — a record missing either cannot be booked.",
    ar: "يعتمد الحجز على تخصص الطبيب والعيادات التي يعمل بها — لا يمكن حجز سجل ينقصه أيٌّ منهما.",
  },

  rosterHeading: { en: "Current clinicians", ar: "الإكلينيكيون الحاليون" },
  rosterEmpty: { en: "No clinicians have been created yet.", ar: "لم يتم إنشاء أي إكلينيكي بعد." },
  name: { en: "Name", ar: "الاسم" },
  type: { en: "Type", ar: "النوع" },
  specialty: { en: "Specialty", ar: "التخصص" },
  clinics: { en: "Clinics", ar: "العيادات" },
  status: { en: "Status", ar: "الحالة" },
  bookable: { en: "Bookable", ar: "قابل للحجز" },
  notBookable: { en: "Not bookable", ar: "غير قابل للحجز" },
  notBookableWhy: {
    en: "Needs a specialty and at least one clinic before the booking screen can offer them.",
    ar: "يحتاج إلى تخصص وعيادة واحدة على الأقل قبل أن يتمكن الحجز من عرضه.",
  },
  none: { en: "None", ar: "لا يوجد" },

  createHeading: { en: "Add a clinician", ar: "إضافة إكلينيكي" },
  account: { en: "User account", ar: "حساب المستخدم" },
  accountHelp: {
    en: "The sign-in account this clinical profile belongs to. One profile per account.",
    ar: "حساب الدخول الذي ينتمي إليه هذا الملف الإكلينيكي. ملف واحد لكل حساب.",
  },
  pickAccount: { en: "Choose an account", ar: "اختر حسابًا" },
  typeLabel: { en: "Clinician type", ar: "نوع الإكلينيكي" },
  doctor: { en: "Doctor", ar: "طبيب" },
  nurse: { en: "Nurse", ar: "ممرض/ة" },
  nameEn: { en: "Full name (English)", ar: "الاسم الكامل (بالإنجليزية)" },
  nameAr: { en: "Full name (Arabic)", ar: "الاسم الكامل (بالعربية)" },
  licence: { en: "Licence number", ar: "رقم الترخيص" },
  licenceHelp: { en: "Optional. Feeds the credential-expiry sweep.", ar: "اختياري. يغذي مراجعة انتهاء الاعتمادات." },
  licenceExpiry: { en: "Licence expiry", ar: "انتهاء الترخيص" },
  primarySpecialty: { en: "Primary specialty", ar: "التخصص الأساسي" },
  pickSpecialty: { en: "Choose a specialty", ar: "اختر تخصصًا" },
  clinicsLegend: { en: "Clinics this clinician works at", ar: "العيادات التي يعمل بها" },
  create: { en: "Create clinician", ar: "إنشاء إكلينيكي" },

  needAccount: { en: "Choose the user account this profile belongs to.", ar: "اختر حساب المستخدم الذي ينتمي إليه هذا الملف." },
  needNames: { en: "Both the English and Arabic name are required.", ar: "الاسم بالإنجليزية والعربية مطلوبان." },
  needSpecialty: { en: "Choose a primary specialty — the booking screen filters on it.", ar: "اختر تخصصًا أساسيًا — يعتمد الحجز عليه." },
  needClinic: { en: "Choose at least one clinic — the booking screen filters on it.", ar: "اختر عيادة واحدة على الأقل — يعتمد الحجز عليها." },

  created: { en: "Clinician created", ar: "تم إنشاء الإكلينيكي" },
  partialTitle: {
    en: "The clinician was created, but part of the assignment did not save:",
    ar: "تم إنشاء الإكلينيكي، لكن جزءًا من التعيين لم يُحفظ:",
  },
  partialFix: {
    en: "The record exists — do NOT submit the form again. Reapply only the failed assignment below.",
    ar: "السجل موجود — لا تُرسل النموذج مرة أخرى. أعد تطبيق التعيين الفاشل أدناه فقط.",
  },
  stepSpecialty: { en: "Specialty", ar: "التخصص" },
  stepBranch: { en: "Clinic", ar: "العيادة" },

  pickClinician: { en: "Choose a clinician from the list to amend their specialties, clinics or status.", ar: "اختر إكلينيكيًا من القائمة لتعديل تخصصاته أو عياداته أو حالته." },
  panelSpecialties: { en: "Specialties", ar: "التخصصات" },
  panelClinics: { en: "Clinics", ar: "العيادات" },
  panelStatus: { en: "Status", ar: "الحالة" },
  primaryTag: { en: "Primary", ar: "أساسي" },
  makePrimary: { en: "Make primary", ar: "جعله أساسيًا" },
  remove: { en: "Remove", ar: "إزالة" },
  add: { en: "Add", ar: "إضافة" },
  addSpecialty: { en: "Add a specialty", ar: "إضافة تخصص" },
  addClinic: { en: "Add a clinic", ar: "إضافة عيادة" },
  noSpecialties: { en: "No specialty assigned — this clinician cannot be booked.", ar: "لا يوجد تخصص — لا يمكن حجز هذا الإكلينيكي." },
  noClinics: { en: "No clinic assigned — this clinician cannot be booked.", ar: "لا توجد عيادة — لا يمكن حجز هذا الإكلينيكي." },
  allSpecialtiesUsed: { en: "Every specialty is already assigned.", ar: "جميع التخصصات معينة بالفعل." },
  allClinicsUsed: { en: "Assigned to every clinic.", ar: "معيّن في جميع العيادات." },
  lastClinicWarning: {
    en: "This is their only clinic. Removing it leaves them unbookable.",
    ar: "هذه عيادته الوحيدة. إزالتها تجعله غير قابل للحجز.",
  },
  revokeBranchNote: {
    en: "Removing a clinic stops NEW bookings there. Appointments already booked are not cancelled — check them before removing.",
    ar: "إزالة العيادة توقف الحجوزات الجديدة بها. المواعيد المحجوزة مسبقًا لا تُلغى — راجعها قبل الإزالة.",
  },
  statusReason: { en: "Reason", ar: "السبب" },
  statusReasonHelp: { en: "Recorded in the audit trail.", ar: "يُسجل في سجل التدقيق." },
  apply: { en: "Apply", ar: "تطبيق" },
  needReason: { en: "A reason is required.", ar: "السبب مطلوب." },
  statusActive: { en: "Active", ar: "نشط" },
  statusSuspended: { en: "Suspended", ar: "موقوف" },
  statusInactive: { en: "Inactive", ar: "غير نشط" },
} satisfies Record<string, Localized>;

const STATUSES = ["Active", "Suspended", "Inactive"] as const;

const TYPES = ["Doctor", "Nurse"] as const;

/**
 * Stable empties for the not-yet-loaded reference lists.
 *
 * `useAsync().data ?? []` mints a NEW array on every render while the load is in flight, so the `useMemo`
 * lookups keyed on these lists would rebuild their Maps every render and the memo would buy nothing — which
 * is precisely what `react-hooks/exhaustive-deps` warns about. One frozen instance each keeps the identity
 * stable until real data replaces it.
 */
const NO_SPECIALTIES: Specialty[] = [];
const NO_BRANCHES: BranchSummary[] = [];
const NO_ACCOUNTS: IdentityUser[] = [];
const NO_PRACTITIONERS: Practitioner[] = [];

/**
 * Phase 14.5 (design 37 §4) — the admin screen for Mersal's own clinicians.
 *
 * <b>Why this screen exists at all.</b> provider-service has exposed the whole practitioner surface since
 * 14.5 — create, assign specialty, assign branch, and the filtered picker that booking reads — and nothing in
 * the web app has ever called any of it. There were zero `practitioner` references in `apps/web`. So the two
 * fields that every specialty→doctor filter on the booking screen depends on could only be set by hand
 * against the API, which is why booking has never been able to offer them.
 *
 * <b>Why specialty and clinics are required here when the API allows neither.</b> The server is right to
 * accept a bare practitioner: a record can legitimately be created before its assignments are known. But a
 * doctor with no specialty and no branch is invisible to `GET /practitioners?branchId=&specialtyCode=`, which
 * is the only query the booking screen makes. Letting the form submit without them produces a record that
 * looks complete in this list and can never be booked — so the form demands both, and the roster column
 * below names the ones that already lack them.
 *
 * <b>Why the account picker is not optional.</b> `Practitioner.UserId` is a logical FK to the identity
 * account and nothing else in the platform sets it. The doctor's own worklist narrows on `?mine=true`, which
 * resolves the practitioner from the TOKEN's subject — so a practitioner row with no user behind it produces
 * a doctor who cannot see their own visits.
 */
export function PractitionerAdmin() {
  const api = useApi();
  const t = useLoc();
  const { lang } = useTheme();
  const write = useWrite();

  const roster = useAsync<Practitioner[]>(() => api.practitioners(), []);
  const specialties = useAsync<Specialty[]>(() => api.specialties(), []);
  const branches = useAsync<BranchSummary[]>(() => api.branches(), []);
  const accounts = useAsync<IdentityUser[]>(() => api.identityUsers(), []);

  const [userId, setUserId] = useState<string | null>(null);
  const [type, setType] = useState<string>("Doctor");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [licenceNo, setLicenceNo] = useState("");
  const [licenceExpiry, setLicenceExpiry] = useState("");
  const [specialtyCode, setSpecialtyCode] = useState<string | null>(null);
  const [branchIds, setBranchIds] = useState<string[]>([]);
  const [attempted, setAttempted] = useState(false);
  const [incomplete, setIncomplete] = useState<PractitionerAttachFailure[] | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  // Re-derived from the RELOADED roster rather than held as its own copy of the row: every panel action
  // reloads the list, and a cached selection would keep painting the pre-edit specialties and clinics after
  // the change it just made.
  const selected = (roster.data ?? NO_PRACTITIONERS).find((p) => p.id === selectedId) ?? null;

  // The three reference lists feed the form's pickers. A failed load leaves a picker with no options, which
  // the required-field check below already refuses to submit past — so the form degrades to "you cannot
  // choose a clinic" rather than throwing, and the roster above still renders.
  const specialtyList = specialties.data ?? NO_SPECIALTIES;
  const branchList = branches.data ?? NO_BRANCHES;
  // De-provisioned accounts are excluded. Attaching a clinical profile to an account that can no longer sign
  // in produces a doctor who appears in the booking picker — the picker reads practitioner rows, which know
  // nothing about the identity store — and can never open the visits booked against them.
  const accountList = useMemo(
    () => (accounts.data ?? NO_ACCOUNTS).filter((u) => u.isActive),
    [accounts.data],
  );

  /** Code → label, so the roster shows "Paediatrics" rather than "PED". */
  const specialtyName = useMemo(() => {
    const m = new Map(specialtyList.map((s) => [s.code, s.name]));
    return (code: string): string => {
      const hit = m.get(code);
      // The code itself is the honest fallback while the reference list is still loading — inventing a
      // dash would read as "this clinician has no specialty", which is a different and worse claim.
      return hit ? t(hit) : code;
    };
  }, [specialtyList, t]);

  const branchName = useMemo(() => {
    const m = new Map(branchList.map((b) => [b.id, b.name]));
    return (id: string): string => {
      const hit = m.get(id);
      return hit ? t(hit) : id;
    };
  }, [branchList, t]);

  const missing = !userId
    ? S.needAccount
    : !nameEn.trim() || !nameAr.trim()
      ? S.needNames
      : !specialtyCode
        ? S.needSpecialty
        : branchIds.length === 0
          ? S.needClinic
          : null;

  function toggleBranch(id: string) {
    setBranchIds((prev) => (prev.includes(id) ? prev.filter((b) => b !== id) : [...prev, id]));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setAttempted(true);
    setIncomplete(null);
    if (missing || !userId || !specialtyCode) return;

    const input: CreatePractitionerInput = {
      userId,
      practitionerType: type as CreatePractitionerInput["practitionerType"],
      fullNameEn: nameEn.trim(),
      fullNameAr: nameAr.trim(),
      licenseNo: licenceNo.trim() || undefined,
      licenseExpiry: licenceExpiry || undefined,
      primarySpecialtyCode: specialtyCode,
      branchIds,
    };

    let partial: PractitionerAttachFailure[] = [];
    const ok = await write.run(async (key) => {
      const r = await api.createPractitioner(input, key);
      partial = r.incomplete;
      return r;
    });

    if (!ok) return;   // the practitioner row itself failed — the form keeps its contents for the retry
    roster.reload();
    if (partial.length > 0) {
      // Deliberately NOT cleared. The record exists, so re-submitting would 409 on the unique user id — but
      // the operator still needs the values in front of them to finish the assignment that did not land.
      setIncomplete(partial);
      return;
    }
    setUserId(null);
    setNameEn("");
    setNameAr("");
    setLicenceNo("");
    setLicenceExpiry("");
    setSpecialtyCode(null);
    setBranchIds([]);
    setAttempted(false);
  }

  const cols: Column<Practitioner>[] = [
    { key: "name", header: t(S.name), cell: (r) => t(r.name), sortable: true },
    { key: "type", header: t(S.type), cell: (r) => t(r.practitionerType === "Nurse" ? S.nurse : S.doctor) },
    {
      key: "specialty",
      header: t(S.specialty),
      cell: (r) => (r.primarySpecialty ? specialtyName(r.primarySpecialty) : <span className="muted">{t(S.none)}</span>),
    },
    {
      key: "clinics",
      header: t(S.clinics),
      cell: (r) =>
        r.branches.length > 0
          ? r.branches.map(branchName).join(" · ")
          : <span className="muted">{t(S.none)}</span>,
    },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      // The column this screen is for. "Bookable" is not a server field — it is the conjunction the booking
      // picker's own query implies, surfaced here so an unbookable doctor is visible as such instead of
      // being discovered when reception cannot find them.
      key: "bookable",
      header: t(S.bookable),
      cell: (r) =>
        r.primarySpecialty && r.branches.length > 0 ? (
          <StatusChip kind="ok" label={t(S.bookable)} />
        ) : (
          <span className="row-actions">
            <StatusChip kind="warn" label={t(S.notBookable)} />
            <span className="muted">{t(S.notBookableWhy)}</span>
          </span>
        ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <p className="muted">{t(S.intro)}</p>

      <div className="split split-wide" style={{ marginTop: "var(--sp3)" }}>
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <h2 className="section-h">{t(S.rosterHeading)}</h2>
          <AsyncSection<Practitioner[]> state={roster} isEmpty={(d) => d.length === 0} emptyLabel={S.rosterEmpty}>
            {(rows) => (
              <DataTable
                columns={cols}
                rows={rows}
                rowKey={(r) => r.id}
                caption={t(S.rosterHeading)}
                interactive
                selectedKey={selectedId ?? undefined}
                onSelect={(r) => setSelectedId(r.id)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selected ? (
            <PractitionerPanel
              key={selected.id}
              practitioner={selected}
              specialties={specialtyList}
              branches={branchList}
              specialtyName={specialtyName}
              branchName={branchName}
              onChanged={roster.reload}
            />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}>
              <p className="muted">{t(S.pickClinician)}</p>
            </Card>
          )}
        </div>
      </div>

      <Card as="section" style={{ padding: "var(--sp5)", marginTop: "var(--sp3)" }}>
        <h2 className="section-h">{t(S.createHeading)}</h2>
        <form onSubmit={submit} className="stack" aria-label={t(S.createHeading)} noValidate>
          <div className="book-field">
            <span className="mrs-label" id="prc-account">{t(S.account)}</span>
            {/* Combobox, not Select: an account list is long and an operator knows the name they are looking
                for, so typing to filter is the only workable interaction (see the note in Combobox). */}
            <Combobox
              aria-labelledby="prc-account"
              options={accountList.map((u) => ({ value: u.id, label: u.displayName, hint: u.username }))}
              value={userId}
              placeholder={t(S.pickAccount)}
              onChange={setUserId}
            />
            <span className="muted">{t(S.accountHelp)}</span>
          </div>

          <div className="book-grid">
            <div className="book-field">
              <span className="mrs-label" id="prc-type">{t(S.typeLabel)}</span>
              <Combobox
                aria-labelledby="prc-type"
                options={TYPES.map((v) => ({ value: v, label: t(v === "Nurse" ? S.nurse : S.doctor) }))}
                value={type}
                onChange={setType}
              />
            </div>
            <div className="book-field">
              <span className="mrs-label" id="prc-specialty">{t(S.primarySpecialty)}</span>
              <Combobox
                aria-labelledby="prc-specialty"
                options={specialtyList.map((s) => ({ value: s.code, label: t(s.name), hint: s.code, keywords: s.code }))}
                value={specialtyCode}
                placeholder={t(S.pickSpecialty)}
                onChange={setSpecialtyCode}
              />
            </div>
          </div>

          <div className="book-grid">
            <InputField label={t(S.nameEn)} requiredMark value={nameEn} onChange={(e) => setNameEn(e.currentTarget.value)} autoComplete="off" />
            <InputField label={t(S.nameAr)} requiredMark value={nameAr} onChange={(e) => setNameAr(e.currentTarget.value)} autoComplete="off" dir="rtl" />
          </div>

          <div className="book-grid">
            <InputField label={t(S.licence)} help={t(S.licenceHelp)} value={licenceNo} onChange={(e) => setLicenceNo(e.currentTarget.value)} autoComplete="off" />
            <InputField label={t(S.licenceExpiry)} type="date" value={licenceExpiry} onChange={(e) => setLicenceExpiry(e.currentTarget.value)} />
          </div>

          {/* Checkboxes rather than a multi-select: there are six clinics, every one fits on screen, and a
              doctor commonly works at two or three — so the whole answer should be visible and directly
              clickable rather than hidden behind a control that has to be opened to be read. */}
          <fieldset className="fieldset">
            <legend>{t(S.clinicsLegend)}</legend>
            {branchList.map((b) => (
              <label key={b.id} className="check">
                <input type="checkbox" checked={branchIds.includes(b.id)} onChange={() => toggleBranch(b.id)} />
                <span>{t(b.name)}</span>
                {b.city && <span className="muted">· {b.city}</span>}
              </label>
            ))}
          </fieldset>

          <div aria-live="polite" className="stack-3">
            {attempted && missing && <InlineAlert tone="warn">{t(missing)}</InlineAlert>}
            {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}

            {/* The partial outcome, rendered as itself. Neither "created" nor "failed" is true here, and
                showing either one sends the operator to exactly the wrong next action. */}
            {incomplete && incomplete.length > 0 && (
              <InlineAlert tone="warn">
                <span>{t(S.partialTitle)}</span>
                <ul className="doc-list" style={{ marginTop: "var(--sp2)" }}>
                  {incomplete.map((f) => (
                    <li key={`${f.step}:${f.ref}`}>
                      <strong>{t(f.step === "specialty" ? S.stepSpecialty : S.stepBranch)}</strong>{" "}
                      {f.step === "branch" ? branchName(f.ref) : specialtyName(f.ref)} — {f.reason}
                    </li>
                  ))}
                </ul>
                <span style={{ display: "block", marginTop: "var(--sp2)" }}>{t(S.partialFix)}</span>
              </InlineAlert>
            )}

            {write.done && !incomplete && <StatusChip kind="ok" label={t(S.created)} />}
            <div>
              <Button type="submit" variant="primary" loading={write.busy}>
                {t(S.create)}
              </Button>
            </div>
          </div>
        </form>
      </Card>
    </>
  );
}

/**
 * Amend one clinician: specialties, clinics, status.
 *
 * <b>Why every action reloads the roster instead of patching local state.</b> Three of these operations have
 * server-side rules the client does not re-implement — the primary specialty cannot be revoked, promoting
 * one demotes whatever held it, and revoking a branch may match more than one assignment row. Painting an
 * optimistic result would mean drawing the outcome this screen GUESSED rather than the one the service
 * applied, and the two diverge precisely in the cases that matter. So the panel shows server-confirmed state
 * only, exactly as the reception desk does after a check-in.
 */
function PractitionerPanel({
  practitioner: p,
  specialties,
  branches,
  specialtyName,
  branchName,
  onChanged,
}: {
  practitioner: Practitioner;
  specialties: Specialty[];
  branches: BranchSummary[];
  specialtyName: (code: string) => string;
  branchName: (id: string) => string;
  onChanged: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { lang } = useTheme();
  const write = useWrite();

  // Which button is spinning. One action at a time: these mutate one aggregate and overlapping writes would
  // race the reload that paints their result.
  const [busy, setBusy] = useState<string | null>(null);
  const [addSpecialty, setAddSpecialty] = useState<string | null>(null);
  const [addClinic, setAddClinic] = useState<string | null>(null);
  const [status, setStatus] = useState<string>(STATUSES[0]);
  const [reason, setReason] = useState("");
  const [reasonMissing, setReasonMissing] = useState(false);

  async function run(key: string, action: () => Promise<unknown>) {
    setBusy(key);
    const ok = await write.run(action);
    setBusy(null);
    if (ok) onChanged();
    return ok;
  }

  const unassignedSpecialties = specialties.filter((s) => !p.specialties.includes(s.code));
  const unassignedBranches = branches.filter((b) => !p.branches.includes(b.id));
  const onlyClinic = p.branches.length === 1;

  async function applyStatus() {
    if (!reason.trim()) {
      setReasonMissing(true);
      return;
    }
    setReasonMissing(false);
    if (await run("status", () => api.setPractitionerStatus(p.id, status, reason.trim()))) setReason("");
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div className="result-head">
        <div>
          <h2 style={{ margin: 0 }}>{t(p.name)}</h2>
          <p className="muted" style={{ margin: "4px 0 0" }}>
            {t(p.practitionerType === "Nurse" ? S.nurse : S.doctor)}
            {p.licenseNo && <> · <span className="tnum">{p.licenseNo}</span></>}
          </p>
        </div>
        <StatusChip kind={p.status.kind} label={t(p.status.label)} />
      </div>

      {/* Errors belong at the top of the panel: any of the controls below can produce one, and a message
          rendered next to only one of them would be missed after using another. */}
      <div aria-live="polite">
        {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
      </div>

      {/* Each block is a NAMED region. The roster beside this panel lists the same specialty and clinic
          names, so without an accessible name for each block there is no way — for a screen-reader user or
          for a test — to say "the Cardiology in the panel" rather than "the Cardiology in the table". */}
      {/* ── Specialties ───────────────────────────────────────────────── */}
      <section aria-labelledby={`h-spec-${p.id}`}>
        <h3 className="section-h" id={`h-spec-${p.id}`}>{t(S.panelSpecialties)}</h3>
        {p.specialties.length === 0 ? (
          <InlineAlert tone="warn">{t(S.noSpecialties)}</InlineAlert>
        ) : (
          <ul className="book-hits">
            {p.specialties.map((code) => {
              const isPrimary = p.primarySpecialty === code;
              return (
                <li key={code}>
                  <span>
                    {specialtyName(code)}{" "}
                    {isPrimary && <StatusChip kind="info" label={t(S.primaryTag)} />}
                  </span>
                  <span className="row-actions">
                    {!isPrimary && (
                      <Button variant="secondary" size="sm" loading={busy === `pri:${code}`}
                              onClick={() => void run(`pri:${code}`, () => api.setPrimarySpecialty(p.id, code))}>
                        {t(S.makePrimary)}
                      </Button>
                    )}
                    {/* The primary has no Remove button: the server refuses it (409), and offering an action
                        that cannot succeed teaches an operator the screen is unreliable. Promote another
                        first — which is exactly what the button beside it does. */}
                    {!isPrimary && (
                      <Button variant="ghost" size="sm" loading={busy === `rms:${code}`}
                              onClick={() => void run(`rms:${code}`, () => api.revokeSpecialty(p.id, code))}>
                        {t(S.remove)}
                      </Button>
                    )}
                  </span>
                </li>
              );
            })}
          </ul>
        )}
        <div className="book-search" style={{ marginBlockStart: "var(--sp3)" }}>
          <div className="book-field" style={{ flex: "1 1 220px" }}>
            <span className="mrs-label" id={`add-spec-${p.id}`}>{t(S.addSpecialty)}</span>
            <Combobox
              aria-labelledby={`add-spec-${p.id}`}
              options={unassignedSpecialties.map((s) => ({ value: s.code, label: t(s.name), hint: s.code, keywords: s.code }))}
              value={addSpecialty}
              placeholder={unassignedSpecialties.length ? t(S.addSpecialty) : t(S.allSpecialtiesUsed)}
              disabled={unassignedSpecialties.length === 0}
              onChange={setAddSpecialty}
            />
          </div>
          <Button
            variant="secondary"
            disabled={!addSpecialty}
            loading={busy === "adds"}
            onClick={() => {
              if (!addSpecialty) return;
              // A practitioner with no primary gets one from the FIRST specialty added: the alternative is
              // adding a specialty and staying unbookable, with nothing on screen saying why.
              const fn = p.primarySpecialty
                ? () => api.assignSpecialty(p.id, addSpecialty)
                : () => api.setPrimarySpecialty(p.id, addSpecialty);
              void run("adds", fn).then((ok) => ok && setAddSpecialty(null));
            }}
          >
            {t(S.add)}
          </Button>
        </div>
      </section>

      {/* ── Clinics ───────────────────────────────────────────────────── */}
      <section aria-labelledby={`h-clin-${p.id}`}>
        <h3 className="section-h" id={`h-clin-${p.id}`}>{t(S.panelClinics)}</h3>
        <p className="muted">{t(S.revokeBranchNote)}</p>
        {p.branches.length === 0 ? (
          <InlineAlert tone="warn">{t(S.noClinics)}</InlineAlert>
        ) : (
          <ul className="book-hits">
            {p.branches.map((id) => (
              <li key={id}>
                <span>{branchName(id)}</span>
                <span className="row-actions">
                  {onlyClinic && <span className="muted">{t(S.lastClinicWarning)}</span>}
                  <Button variant="ghost" size="sm" loading={busy === `rmb:${id}`}
                          onClick={() => void run(`rmb:${id}`, () => api.revokePractitionerBranch(p.id, id))}>
                    {t(S.remove)}
                  </Button>
                </span>
              </li>
            ))}
          </ul>
        )}
        <div className="book-search" style={{ marginBlockStart: "var(--sp3)" }}>
          <div className="book-field" style={{ flex: "1 1 220px" }}>
            <span className="mrs-label" id={`add-branch-${p.id}`}>{t(S.addClinic)}</span>
            <Combobox
              aria-labelledby={`add-branch-${p.id}`}
              options={unassignedBranches.map((b) => ({ value: b.id, label: t(b.name), hint: b.city }))}
              value={addClinic}
              placeholder={unassignedBranches.length ? t(S.addClinic) : t(S.allClinicsUsed)}
              disabled={unassignedBranches.length === 0}
              onChange={setAddClinic}
            />
          </div>
          <Button
            variant="secondary"
            disabled={!addClinic}
            loading={busy === "addb"}
            onClick={() => {
              if (!addClinic) return;
              void run("addb", () => api.assignPractitionerBranch(p.id, addClinic)).then((ok) => ok && setAddClinic(null));
            }}
          >
            {t(S.add)}
          </Button>
        </div>
      </section>

      {/* ── Status ────────────────────────────────────────────────────── */}
      <section aria-labelledby={`h-stat-${p.id}`}>
        <h3 className="section-h" id={`h-stat-${p.id}`}>{t(S.panelStatus)}</h3>
        <div className="book-search">
          <div className="book-field" style={{ flex: "1 1 160px" }}>
            <span className="mrs-label" id={`status-${p.id}`}>{t(S.panelStatus)}</span>
            <Combobox
              aria-labelledby={`status-${p.id}`}
              options={STATUSES.map((s) => ({
                value: s,
                label: t(s === "Active" ? S.statusActive : s === "Suspended" ? S.statusSuspended : S.statusInactive),
              }))}
              value={status}
              onChange={setStatus}
            />
          </div>
          <InputField
            label={t(S.statusReason)}
            help={t(S.statusReasonHelp)}
            value={reason}
            error={reasonMissing ? t(S.needReason) : undefined}
            onChange={(e) => setReason(e.currentTarget.value)}
            autoComplete="off"
          />
          <Button variant="secondary" loading={busy === "status"} onClick={() => void applyStatus()}>
            {t(S.apply)}
          </Button>
        </div>
      </section>
    </Card>
  );
}
