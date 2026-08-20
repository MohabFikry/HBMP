import { useMemo, useState } from "react";
import {
  Button, Combobox, ComboboxField, Icon, InlineAlert, InputField, Modal, useToast,
} from "@mersal/design-system";
import type {
  AllergenOption, AllergyRecord, AllergySeverity, BloodGroup, Localized, MedicationSource,
  PrescribableDrug,
} from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { DrugCombobox } from "../prescribing/DrugCombobox";
import { useAsync } from "../../api/useAsync";
import { useLoc } from "../_shared";

/**
 * Blood group and allergies — the standing clinical facts, on the MEMBER's file rather than on the visit.
 *
 * <b>The distinction this panel exists to hold.</b> "No allergies recorded" and "this patient has no
 * allergies" are different clinical claims, and only the first is ever true of an empty list. A panel that
 * renders an empty list as a calm blank tells a prescriber the second one. So absence is rendered
 * explicitly, in words, with the dashed-and-hollow treatment the prescribing workspace already uses for its
 * unanswered states (`NotChecked` / `Unavailable`) — it is the same idea in a second place, and it looks
 * like it.
 *
 * The link is not decorative: prescribe-time allergy screening reports `NotChecked` at zero recorded
 * allergens for exactly this reason, so recording one here is what turns that check into a real answer. This
 * panel is where a doctor closes that gap, which is why it sits in the encounter and not only in the file.
 *
 * Everything written here goes to emr at the beneficiary level — the member's file, not this encounter — so
 * it is there for the next clinician who opens the patient, in any workspace.
 */

const S = {
  title: { en: "Allergies & blood group", ar: "الحساسية وفصيلة الدم" },
  bloodGroup: { en: "Blood group", ar: "فصيلة الدم" },
  bloodGroupSet: { en: "Set blood group", ar: "تسجيل فصيلة الدم" },
  bloodGroupNone: { en: "Not recorded", ar: "غير مسجّلة" },
  addAllergy: { en: "Add allergy", ar: "إضافة حساسية" },
  noAllergies: {
    en: "No allergies recorded — not the same as none.",
    ar: "لا توجد حساسية مسجّلة — وهذا لا يعني عدم وجودها.",
  },
  noAllergiesHint: {
    en: "Nobody has recorded an allergy history for this patient. Prescribe-time allergy screening reports "
      + "\"not checked\" until one is recorded.",
    ar: "لم يسجّل أحد تاريخ الحساسية لهذا المريض. سيظل فحص الحساسية عند الوصف يُظهر «لم يتم التحقق» حتى تُسجَّل.",
  },
  allergen: { en: "Allergen", ar: "المادة المسبّبة" },
  severity: { en: "Severity", ar: "الشدة" },
  reaction: { en: "Reaction (optional)", ar: "التفاعل (اختياري)" },
  reactionPh: { en: "e.g. rash, swelling", ar: "مثال: طفح جلدي، تورّم" },
  save: { en: "Save", ar: "حفظ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  chooseAllergen: { en: "Choose an allergen.", ar: "اختر المادة المسبّبة." },
  chooseBloodGroup: { en: "Choose a blood group.", ar: "اختر فصيلة الدم." },
  saveFailed: { en: "Could not save. Try again.", ar: "تعذّر الحفظ. حاول مرة أخرى." },
  catalogueFailed: {
    en: "The allergen list could not be loaded, so an allergy cannot be recorded right now.",
    ar: "تعذّر تحميل قائمة المواد المسبّبة، لذلك لا يمكن تسجيل حساسية الآن.",
  },
  loadFailed: {
    en: "Allergies and blood group could not be loaded. This is NOT a report that there are none.",
    ar: "تعذّر تحميل الحساسية وفصيلة الدم. هذا ليس تقريرًا بعدم وجودها.",
  },
  allergySaved: { en: "Allergy recorded on the member's file.", ar: "تم تسجيل الحساسية في ملف العضو." },
  bloodGroupSaved: { en: "Blood group recorded.", ar: "تم تسجيل فصيلة الدم." },
  unspecified: { en: "(unspecified)", ar: "(غير محدّد)" },
  categoryDrug: { en: "Drug", ar: "دواء" },
  categoryFood: { en: "Food", ar: "طعام" },
  categoryEnvironmental: { en: "Environmental", ar: "بيئي" },
  medsTitle: { en: "Current medications", ar: "الأدوية الحالية" },
  addMedication: { en: "Add medication", ar: "إضافة دواء" },
  medicine: { en: "Medicine", ar: "الدواء" },
  medicineSearch: { en: "Search by name", ar: "ابحث بالاسم" },
  source: { en: "Source", ar: "المصدر" },
  srcPrescribed: { en: "Prescribed by Mersal", ar: "موصوف من مرسال" },
  srcSelfReported: { en: "Self-reported", ar: "ذكره المريض" },
  srcExternal: { en: "External prescriber", ar: "واصف خارجي" },
  started: { en: "Started (optional)", ar: "تاريخ البدء (اختياري)" },
  markStopped: { en: "Mark stopped", ar: "تسجيل التوقف" },
  medStopped: { en: "Recorded as stopped.", ar: "تم تسجيل التوقف." },
  medSaved: { en: "Medication recorded on the member's file.", ar: "تم تسجيل الدواء في ملف العضو." },
  chooseMedicine: { en: "Choose a medicine.", ar: "اختر الدواء." },
  noMeds: {
    en: "No medications recorded — not the same as taking none.",
    ar: "لا توجد أدوية مسجّلة — وهذا لا يعني عدم تناوله أي دواء.",
  },
  noMedsHint: {
    en: "Nobody has recorded what this patient is already taking. Prescribe-time interaction screening says "
      + "so rather than reporting a clean check.",
    ar: "لم يسجّل أحد ما يتناوله هذا المريض بالفعل. سيوضّح فحص التداخلات عند الوصف ذلك بدلًا من الإبلاغ عن فحص سليم.",
  },
  medsLoadFailed: {
    en: "The medication list could not be loaded. This is NOT a report that there are none.",
    ar: "تعذّر تحميل قائمة الأدوية. هذا ليس تقريرًا بعدم وجودها.",
  },
  sevMild: { en: "Mild", ar: "خفيفة" },
  sevModerate: { en: "Moderate", ar: "متوسطة" },
  sevSevere: { en: "Severe", ar: "شديدة" },
} satisfies Record<string, Localized>;

const SEVERITY_LABEL: Record<AllergySeverity, Localized> = {
  Mild: S.sevMild, Moderate: S.sevModerate, Severe: S.sevSevere,
};

const CATEGORY_LABEL: Record<AllergenOption["category"], Localized> = {
  Drug: S.categoryDrug, Food: S.categoryFood, Environmental: S.categoryEnvironmental,
};

const BLOOD_GROUPS: BloodGroup[] = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];

export function MemberClinicalPanel({
  beneficiaryId,
  /** Called after a successful write, so the patient context bar re-reads the same facts. */
  onRecorded,
}: {
  beneficiaryId: string;
  onRecorded?: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync(
    () => api.memberClinicalRecord(beneficiaryId),
    [beneficiaryId],
  );

  function afterWrite() {
    state.reload();
    onRecorded?.();
  }

  // A FAILED read is not an empty record. Rendering the panel's normal empty state here would turn an outage
  // into the sentence "no allergies recorded", which is the exact substitution the whole design forbids.
  if (state.status === "error") {
    return (
      <section className="mc-panel" aria-label={t(S.title)}>
        <InlineAlert tone="bad">{t(S.loadFailed)}</InlineAlert>
      </section>
    );
  }

  const record = state.data;
  const allergies = record?.allergies ?? [];

  return (
    <section className="mc-panel" aria-label={t(S.title)}>
      <BloodGroupControl
        beneficiaryId={beneficiaryId}
        current={record?.bloodGroup ?? null}
        loading={state.status === "loading"}
        onSaved={afterWrite}
      />

      <div className="mc-divider" aria-hidden="true" />

      {/* aria-live: an allergy recorded from the dialog changes THIS list, and the dialog that changed it has
          already closed. Without the announcement a screen-reader user gets a toast and no evidence. */}
      <ul className="mc-allergies" aria-live="polite">
        {state.status === "loading" ? null : allergies.length === 0 ? (
          <li className="mc-empty" title={t(S.noAllergiesHint)}>
            {/* Hollow glyph + dashed border — the SHAPE says "no answer" before any colour or word is read,
                matching LineStatusChip's unanswered states. Four cues, never colour alone. */}
            <span className="mc-empty-glyph" aria-hidden="true">○</span>
            {t(S.noAllergies)}
          </li>
        ) : (
          allergies.map((a) => <AllergyChip key={a.allergyId} allergy={a} t={t} />)
        )}
      </ul>

      <AddAllergyControl beneficiaryId={beneficiaryId} onSaved={afterWrite} />

      <div className="mc-divider" aria-hidden="true" />

      {/* 32.2 — the third standing fact, and the newest reason this panel exists. Allergies turn the
          prescribe-time allergy check from NotChecked into an answer; this list does the same for the
          interaction check, which until 32.1 compared every prescription against nothing and reported "no
          interaction found". Same panel, same rule about absence, because it is the same kind of claim. */}
      <CurrentMedications beneficiaryId={beneficiaryId} onChanged={afterWrite} />
    </section>
  );
}

const SOURCE_LABEL: Record<MedicationSource, Localized> = {
  Prescribed: S.srcPrescribed, SelfReported: S.srcSelfReported, External: S.srcExternal,
};

/**
 * What the patient is already taking.
 *
 * <p>Reads its own list rather than taking one from <c>memberClinicalRecord</c>: the medication list is
 * filtered to Active for this purpose, and a stopped medicine has to leave it without leaving the record.
 * Folding it into the clinical record read would have meant either sending both and filtering on screen —
 * which puts a clinical decision in the client — or losing the stopped rows entirely.</p>
 */
function CurrentMedications({
  beneficiaryId, onChanged,
}: {
  beneficiaryId: string;
  onChanged: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [nonce, setNonce] = useState(0);
  const state = useAsync(() => api.medicationHistory(beneficiaryId, "Active"), [beneficiaryId, nonce]);
  const [stopping, setStopping] = useState<string | null>(null);

  function reload() {
    setNonce((n) => n + 1);
    onChanged();
  }

  async function stop(medHistoryId: string) {
    setStopping(medHistoryId);
    try {
      await api.stopMedication(beneficiaryId, medHistoryId);
      toast(t(S.medStopped));
      reload();
    } catch {
      toast(t(S.saveFailed));
    } finally {
      setStopping(null);
    }
  }

  // A failed read is not an empty list — the same rule the allergy half holds, and for a list the
  // interaction check reads it matters more, not less.
  if (state.status === "error") {
    return (
      <div className="mc-meds" aria-label={t(S.medsTitle)}>
        <InlineAlert tone="bad">{t(S.medsLoadFailed)}</InlineAlert>
      </div>
    );
  }

  const rows = state.data ?? [];

  return (
    <div className="mc-meds">
      <h3 className="mc-meds-title">{t(S.medsTitle)}</h3>
      <ul className="mc-med-list" aria-label={t(S.medsTitle)} aria-live="polite">
        {state.status === "loading" ? null : rows.length === 0 ? (
          <li className="mc-empty" title={t(S.noMedsHint)}>
            <span className="mc-empty-glyph" aria-hidden="true">○</span>
            {t(S.noMeds)}
          </li>
        ) : (
          rows.map((m) => (
            <li className="mc-med" key={m.medHistoryId} aria-label={m.drugName ?? m.drugId}>
              <span aria-hidden="true" className="mc-med-icon">◆</span>
              <span className="mc-med-name">{m.drugName ?? m.drugId}</span>
              {/* The source is rendered because the interaction warning names it. A prescriber weighing
                  "interacts with St John's Wort" needs to know that came from the patient's own account
                  rather than from a dispensing record. */}
              <span className="mc-med-source">{t(SOURCE_LABEL[m.source])}</span>
              {m.startDate ? <span className="mc-med-since">· {m.startDate}</span> : null}
              <Button
                variant="ghost"
                size="sm"
                disabled={stopping === m.medHistoryId}
                onClick={() => void stop(m.medHistoryId)}
              >
                {t(S.markStopped)}
              </Button>
            </li>
          ))
        )}
      </ul>
      <AddMedicationControl beneficiaryId={beneficiaryId} onSaved={reload} />
    </div>
  );
}

/**
 * Record a medicine the patient is already on.
 *
 * <p>The source select has no default. "Prescribed by Mersal" would be wrong for the case this control
 * exists to capture — a medicine Mersal did not prescribe is the half of the list no query over our own data
 * can reconstruct — and defaulting to it would quietly relabel exactly the rows that matter.</p>
 */
function AddMedicationControl({
  beneficiaryId, onSaved,
}: {
  beneficiaryId: string;
  onSaved: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [drug, setDrug] = useState<PrescribableDrug | null>(null);
  const [source, setSource] = useState<MedicationSource | "">("");
  const [startDate, setStartDate] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  async function save() {
    if (!drug) { setError(S.chooseMedicine); return; }
    if (!source) { setError(S.chooseMedicine); return; }
    setBusy(true);
    try {
      await api.addMedicationHistory(beneficiaryId, {
        drugId: drug.drugId,
        source,
        startDate: startDate.trim() ? startDate.trim() : null,
        endDate: null,
        status: "Active",
      });
      toast(t(S.medSaved));
      setOpen(false);
      setDrug(null);
      setSource("");
      setStartDate("");
      setError(null);
      onSaved();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Button
        variant="secondary"
        size="sm"
        leadingIcon={<Icon name="plus" aria-hidden="true" />}
        onClick={() => setOpen(true)}
      >
        {t(S.addMedication)}
      </Button>
      <Modal open={open} onOpenChange={setOpen} title={t(S.addMedication)}>
        {error ? <InlineAlert tone="bad">{t(error)}</InlineAlert> : null}
        {/* The prescribing workspace's own picker (26.2), not a second one. It already searches by trade name
            OR active ingredient, shows the ingredient and price under the name, and is the control a doctor
            on this platform has learned to use to name a medicine. A different drug field here would be a
            second answer to "how do I find a medicine", which is the shape of duplication doc 46 §7b refuses
            for notes and the same argument applies. */}
        <DrugCombobox value={drug} onChange={setDrug} />
        <ComboboxField
          label={t(S.source)}
          value={source === "" ? null : source}
          onChange={(v) => setSource(v as MedicationSource)}
          placeholder="—"
          options={[
            { value: "Prescribed", label: t(S.srcPrescribed) },
            { value: "SelfReported", label: t(S.srcSelfReported) },
            { value: "External", label: t(S.srcExternal) },
          ]}
        />
        <InputField
          label={t(S.started)}
          type="date"
          value={startDate}
          onChange={(e) => setStartDate(e.target.value)}
        />
        <div className="mc-actions">
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button
            onClick={() => void save()}
            disabled={busy}
            leadingIcon={<Icon name="check2" aria-hidden="true" />}
          >
            {t(S.save)}
          </Button>
        </div>
      </Modal>
    </>
  );
}

function AllergyChip({ allergy, t }: { allergy: AllergyRecord; t: (l: Localized) => string }) {
  // Severity is a fourth cue on top of hue/icon/word, carried as a data attribute so CSS can weight a
  // Severe allergy more heavily without severity ever being communicated by colour alone.
  return (
    <li className="mc-allergy" data-severity={allergy.severity}>
      <span aria-hidden="true" className="mc-allergy-icon">⚠</span>
      <span className="mc-allergy-name">{allergy.allergen ?? t(S.unspecified)}</span>
      <span className="mc-allergy-sev">{t(SEVERITY_LABEL[allergy.severity])}</span>
      {allergy.reaction ? <span className="mc-allergy-reaction">· {allergy.reaction}</span> : null}
    </li>
  );
}

/**
 * Blood group: a value, or an explicit "not recorded", and a control to set it.
 *
 * The unrecorded state is a BUTTON reading "Blood group — not recorded", not a blank. A clinician who needs
 * the value and finds nothing should be one click from fixing that, in the place they discovered it missing.
 */
function BloodGroupControl({
  beneficiaryId, current, loading, onSaved,
}: {
  beneficiaryId: string;
  current: string | null;
  loading: boolean;
  onSaved: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [choice, setChoice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  async function save() {
    if (!choice) {
      setError(S.chooseBloodGroup);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api.setBloodGroup(beneficiaryId, choice as BloodGroup);
      setOpen(false);
      toast(t(S.bloodGroupSaved), "ok");
      onSaved();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        // Reopen on the RECORDED value, not on the last thing that was clicked and abandoned.
        if (next) { setChoice(current); setError(null); }
      }}
      title={t(S.bloodGroupSet)}
      trigger={
        <button
          type="button"
          className="mc-blood"
          data-recorded={current ? "yes" : "no"}
          disabled={loading}
          aria-label={`${t(S.bloodGroup)}: ${current ?? t(S.bloodGroupNone)}`}
        >
          <Icon name="droplet" width={16} height={16} aria-hidden="true" />
          <span className="mc-blood-label">{t(S.bloodGroup)}</span>
          <span className="mc-blood-value">{current ?? t(S.bloodGroupNone)}</span>
          <span className="mc-blood-edit" aria-hidden="true">✎</span>
        </button>
      }
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button leadingIcon={<Icon name="check2" />} variant="primary" loading={busy} onClick={() => void save()}>{t(S.save)}</Button>
        </>
      }
    >
      <div className="stack-3">
        {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
        <Combobox
          aria-label={t(S.bloodGroup)}
          value={choice}
          onChange={setChoice}
          placeholder={t(S.bloodGroupNone)}
          options={BLOOD_GROUPS.map((g) => ({ value: g, label: g }))}
        />
      </div>
    </Modal>
  );
}

/**
 * Record an allergy against the member's file.
 *
 * The allergen comes from the masterdata catalogue and is never free text: prescribe-time screening matches
 * a drug's ATC chain against a Drug-category allergen's code, and a typed substance name matches nothing —
 * it would look recorded and screen against nothing at all.
 */
function AddAllergyControl({ beneficiaryId, onSaved }: { beneficiaryId: string; onSaved: () => void }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [allergenId, setAllergenId] = useState<string | null>(null);
  const [severity, setSeverity] = useState<AllergySeverity>("Moderate");
  const [reaction, setReaction] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  // Fetched once the dialog is first opened rather than on mount: the catalogue is only needed to WRITE, and
  // the panel is on every encounter screen whether or not anyone records anything.
  const [wanted, setWanted] = useState(false);
  const catalogue = useAsync<AllergenOption[]>(
    () => (wanted ? api.allergenCatalogue() : Promise.resolve([])),
    [wanted],
  );

  // Grouped by category, so a list mixing penicillin with peanuts is scannable. Sorted within the group by
  // the label the reader will actually see.
  const options = useMemo(() => {
    const rows = catalogue.data ?? [];
    return [...rows]
      .sort((a, b) =>
        a.category.localeCompare(b.category) || a.name.localeCompare(b.name))
      .map((a) => ({ value: a.allergenId, label: a.name, hint: t(CATEGORY_LABEL[a.category]) }));
  }, [catalogue.data, t]);

  async function save() {
    if (!allergenId) {
      setError(S.chooseAllergen);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api.addAllergy(beneficiaryId, {
        allergenId,
        reaction: reaction.trim() || null,
        severity,
        // Recorded ACTIVE. An allergy a clinician is entering during a consultation is one they believe in
        // now; Inactive/Resolved are states a later reviewer moves it to, not states you record it in.
        status: "Active",
      });
      setOpen(false);
      setAllergenId(null);
      setReaction("");
      setSeverity("Moderate");
      toast(t(S.allergySaved), "ok");
      onSaved();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (next) { setWanted(true); setError(null); }
      }}
      title={t(S.addAllergy)}
      trigger={
        <Button
          variant="ghost"
          size="sm"
          className="mc-add"
          leadingIcon={<Icon name="plus" aria-hidden="true" />}
        >
          {t(S.addAllergy)}
        </Button>
      }
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button leadingIcon={<Icon name="check2" />}
            variant="primary"
            loading={busy}
            disabled={catalogue.status === "error"}
            onClick={() => void save()}
          >
            {t(S.save)}
          </Button>
        </>
      }
    >
      <div className="stack-3">
        {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
        {/* A catalogue that did not load is stated, not silently rendered as an empty picker — an empty
            picker reads as "there are no allergens", which is never true. */}
        {catalogue.status === "error" && <InlineAlert tone="bad">{t(S.catalogueFailed)}</InlineAlert>}

        <label className="mc-field">
          <span className="mc-field-label">{t(S.allergen)}</span>
          <Combobox
            aria-label={t(S.allergen)}
            value={allergenId}
            onChange={setAllergenId}
            placeholder={t(S.allergen)}
            disabled={catalogue.status !== "success" || options.length === 0}
            options={options}
          />
        </label>

        <label className="mc-field">
          <span className="mc-field-label">{t(S.severity)}</span>
          <Combobox
            aria-label={t(S.severity)}
            value={severity}
            onChange={(v) => setSeverity(v as AllergySeverity)}
            options={(["Mild", "Moderate", "Severe"] as AllergySeverity[])
              .map((s) => ({ value: s, label: t(SEVERITY_LABEL[s]) }))}
          />
        </label>

        <InputField
          label={t(S.reaction)}
          type="text"
          maxLength={120}
          value={reaction}
          placeholder={t(S.reactionPh)}
          onChange={(e) => setReaction(e.currentTarget.value)}
        />
      </div>
    </Modal>
  );
}
