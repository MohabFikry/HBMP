import { useState } from "react";
import { Button, Card, DataTable, Icon, InlineAlert, InputField, Modal, StatusChip, TextareaField } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, ProgramEnablement, ProgramFeature, ProgramLimit } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite } from "../api/useWrite";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

/**
 * Phase 21.6 — programme enablement administration (design 40 §4, adaptation A4).
 *
 * A4 IS THE POINT, and it is a copy decision as much as a code one: this is NOT a commercial plan screen.
 * Mersal is a charity and these tenants are partner NGOs and clinics, not customers — so nothing here says
 * plan, upgrade, tier, billing or trial. A refusal downstream reads "contact Mersal programme
 * administration", never "upgrade your plan", and the a11y/copy test asserts the absence of that vocabulary.
 *
 * PLATFORM ADMINISTRATION ONLY. The server requires the platform-admin role for every write on this screen
 * (a tenant that can switch on its own programmes is not gated at all). The hiding here is cosmetic, per
 * §6 — the API refuses regardless, and the standing test hand-crafts the request to prove it.
 *
 * ENABLEMENT NEVER GRANTS. Switching a module on does not give anybody a permission; it only stops the
 * third gate refusing. That sentence is on the screen because the two are routinely confused, and an
 * administrator who confuses them will toggle a feature to fix what is actually a missing role.
 */

const S = {
  title: { en: "Programme Enablement", ar: "تفعيل البرامج" },
  intro: {
    en: "Which programmes this organisation has been onboarded onto, and the capacity agreed with it. Enabling a programme never grants anyone a permission — roles still decide that.",
    ar: "البرامج التي انضمّت إليها هذه المؤسسة، والسعة المتفق عليها معها. تفعيل برنامج لا يمنح أي شخص صلاحية — الأدوار وحدها تقرّر ذلك.",
  },
  tenantLabel: { en: "Organisation", ar: "المؤسسة" },
  featuresHeading: { en: "Programmes", ar: "البرامج" },
  limitsHeading: { en: "Capacity", ar: "السعة" },
  feature: { en: "Programme", ar: "البرنامج" },
  state: { en: "State", ar: "الحالة" },
  on: { en: "Enabled", ar: "مُفعّل" },
  off: { en: "Not enabled", ar: "غير مُفعّل" },
  neverSet: { en: "Never configured", ar: "لم يُضبط قط" },
  changedBy: { en: "Last changed by", ar: "آخر تغيير بواسطة" },
  enable: { en: "Enable", ar: "تفعيل" },
  disable: { en: "Disable", ar: "إيقاف" },

  limit: { en: "Capacity", ar: "السعة" },
  cap: { en: "Cap", ar: "الحد" },
  usage: { en: "In use", ar: "المستخدم" },
  unlimited: { en: "No cap", ar: "بلا حد" },
  notCounted: { en: "Not counted here", ar: "لا يُحتسب هنا" },
  notCountedHelp: {
    en: "This figure is owned by another service and is not measured on this screen — it is not zero.",
    ar: "هذا الرقم تملكه خدمة أخرى ولا يُقاس في هذه الشاشة — وهو ليس صفرًا.",
  },
  overCap: { en: "Over cap", ar: "تجاوز الحد" },
  setCap: { en: "Set the cap", ar: "ضبط الحد" },
  capValue: { en: "Maximum", ar: "الحد الأقصى" },

  reason: { en: "Reason", ar: "السبب" },
  reasonRequired: {
    en: "A reason is required — an enablement change without one cannot be reviewed later.",
    ar: "السبب مطلوب — لا يمكن مراجعة تغيير التفعيل بدونه.",
  },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  enableTitle: { en: "Enable this programme?", ar: "تفعيل هذا البرنامج؟" },
  disableTitle: { en: "Disable this programme?", ar: "إيقاف هذا البرنامج؟" },
  disableWarning: {
    en: "Everyone in this organisation loses the module at their next request. Existing records are kept and stay in the audit trail — nothing is deleted.",
    ar: "سيفقد جميع أفراد هذه المؤسسة الوحدة عند طلبهم التالي. تُحفظ السجلات القائمة وتبقى في سجل التدقيق — لا يُحذف شيء.",
  },
  typeToConfirm: { en: "Type the organisation’s name to confirm", ar: "اكتب اسم المؤسسة للتأكيد" },
  typeMismatch: { en: "That does not match the organisation’s name.", ar: "هذا لا يطابق اسم المؤسسة." },
  capTitle: { en: "Change the capacity?", ar: "تغيير السعة؟" },
  capBelowUsage: {
    en: "This cap is below current usage. Nothing is removed — the cap refuses the next addition only.",
    ar: "هذا الحد أقل من الاستخدام الحالي. لا يُزال شيء — الحد يرفض الإضافة التالية فقط.",
  },
  empty: { en: "No programmes configured.", ar: "لا توجد برامج مضبوطة." },
} satisfies Record<string, Localized>;

/** The tenant whose enablement is being administered. Read from the route in the real portal shell. */
export function ProgramAdmin({ tenant = "mersal" }: { tenant?: string }) {
  const api = useApi();
  const t = useLoc();
  const [reloadKey, setReloadKey] = useState(0);
  const state = useAsync<ProgramEnablement>(() => api.programEnablement(tenant), [tenant, reloadKey]);

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted" style={{ marginTop: 0 }}>{t(S.intro)}</p>
        <p style={{ margin: 0 }}>
          {t(S.tenantLabel)}: <span className="tnum">{tenant}</span>
        </p>
      </Card>

      <AsyncSection<ProgramEnablement> state={state} isEmpty={(d) => d.features.length === 0} emptyLabel={S.empty}>
        {(data) => (
          <>
            <FeatureTable tenant={tenant} features={data.features} onChanged={() => setReloadKey((k) => k + 1)} />
            <LimitTable tenant={tenant} limits={data.limits} onChanged={() => setReloadKey((k) => k + 1)} />
          </>
        )}
      </AsyncSection>
    </>
  );
}

function FeatureTable({
  tenant,
  features,
  onChanged,
}: {
  tenant: string;
  features: ProgramFeature[];
  onChanged: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const [target, setTarget] = useState<ProgramFeature | null>(null);
  const [reason, setReason] = useState("");
  const [typed, setTyped] = useState("");
  const [touched, setTouched] = useState(false);

  // Turning a programme OFF is the destructive direction — it removes a module from a whole organisation —
  // so it takes the typed-name confirmation. Turning one ON is additive and takes an ordinary confirm.
  const destructive = target?.enabled === true;
  const reasonError = touched && !reason.trim() ? t(S.reasonRequired) : undefined;
  const typedError = touched && destructive && typed.trim() !== tenant ? t(S.typeMismatch) : undefined;

  const close = () => {
    setTarget(null);
    setReason("");
    setTyped("");
    setTouched(false);
    write.reset();
  };

  const confirm = async () => {
    setTouched(true);
    if (!target || !reason.trim()) return;
    if (destructive && typed.trim() !== tenant) return;
    const ok = await write.run(() => api.setProgramFeature(tenant, target.key, !target.enabled, reason.trim()));
    if (ok) {
      close();
      onChanged();
    }
  };

  const cols: Column<ProgramFeature>[] = [
    { key: "feature", header: t(S.feature), cell: (r) => <span className="mono">{r.key}</span> },
    {
      key: "state",
      header: t(S.state),
      cell: (r) =>
        r.enabled ? (
          <StatusChip kind="ok" label={t(S.on)} />
        ) : r.configured ? (
          <StatusChip kind="neu" label={t(S.off)} />
        ) : (
          // Never configured is its OWN state. "Nobody has decided" and "someone decided no" are different
          // conversations with the partner organisation, and collapsing them loses the distinction.
          <StatusChip kind="warn" label={t(S.neverSet)} />
        ),
    },
    { key: "changedBy", header: t(S.changedBy), cell: (r) => <span className="muted">{r.changedBy ?? "—"}</span> },
    {
      key: "action",
      header: "",
      cell: (r) => (
        <Button variant={r.enabled ? "danger" : "secondary"} size="sm" onClick={() => setTarget(r)}>
          {t(r.enabled ? S.disable : S.enable)}
        </Button>
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp3)" }}>
      <h2 className="panel-h">{t(S.featuresHeading)}</h2>
      <DataTable columns={cols} rows={features} rowKey={(r) => r.key} caption={t(S.featuresHeading)} />

      <Modal
        open={target !== null}
        onOpenChange={(o) => !o && close()}
        title={t(destructive ? S.disableTitle : S.enableTitle)}
        footer={
          <>
            <Button variant="ghost" onClick={close}>{t(S.cancel)}</Button>
            <Button variant={destructive ? "danger" : "primary"} onClick={confirm} disabled={write.busy}>
              {t(S.confirm)}
            </Button>
          </>
        }
      >
        {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}
        <p className="mono">{target?.key}</p>
        {destructive ? <InlineAlert tone="warn">{t(S.disableWarning)}</InlineAlert> : null}
        <TextareaField label={t(S.reason)} value={reason} error={reasonError} onChange={(e) => setReason(e.currentTarget.value)} />
        {destructive ? (
          <InputField
            label={t(S.typeToConfirm)}
            help={tenant}
            value={typed}
            error={typedError}
            onChange={(e) => setTyped(e.currentTarget.value)}
          />
        ) : null}
      </Modal>
    </Card>
  );
}

function LimitTable({ tenant, limits, onChanged }: { tenant: string; limits: ProgramLimit[]; onChanged: () => void }) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const [target, setTarget] = useState<ProgramLimit | null>(null);
  const [value, setValue] = useState("");
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);

  const parsed = Number(value);
  const valid = value.trim() !== "" && Number.isInteger(parsed) && parsed >= 0;
  const reasonError = touched && !reason.trim() ? t(S.reasonRequired) : undefined;
  const belowUsage = valid && target?.currentUsage != null && parsed < target.currentUsage;

  const close = () => {
    setTarget(null);
    setValue("");
    setReason("");
    setTouched(false);
    write.reset();
  };

  const confirm = async () => {
    setTouched(true);
    if (!target || !valid || !reason.trim()) return;
    const ok = await write.run(() => api.setProgramLimit(tenant, target.key, parsed, reason.trim()));
    if (ok) {
      close();
      onChanged();
    }
  };

  const cols: Column<ProgramLimit>[] = [
    { key: "limit", header: t(S.limit), cell: (r) => <span className="mono">{r.key}</span> },
    // Cap and usage are read ACROSS as a pair and DOWN as two columns of magnitudes, so both are `numeric`:
    // the figures stack at the same edge and "which limit is close to its ceiling" becomes a shape rather
    // than a string comparison. They were `.tnum` in start-aligned cells, which sets the figure width and
    // leaves the column ragged — equal-width digits that never line up.
    {
      key: "cap",
      header: t(S.cap),
      numeric: true,
      cell: (r) => (r.maxValue === null ? <span className="muted">{t(S.unlimited)}</span> : r.maxValue),
    },
    {
      key: "usage",
      header: t(S.usage),
      numeric: true,
      cell: (r) =>
        r.currentUsage === null ? (
          // NOT rendered as 0. Null means the answering service does not own this count, and a zero here
          // would tell an administrator the organisation was idle when nobody actually measured.
          <span className="muted" title={t(S.notCountedHelp)}>{t(S.notCounted)}</span>
        ) : (
          <>
            {/* The chip goes BEFORE the figure. In an end-aligned cell the last thing in the cell is what
                touches the edge, so a trailing chip pushes the number inboard by its own width — and the one
                row that most needs to be comparable with the rest is the one that has broken out of it. */}
            {r.maxValue !== null && r.currentUsage > r.maxValue ? (
              <><StatusChip kind="bad" label={t(S.overCap)} /> </>
            ) : null}
            {r.currentUsage}
          </>
        ),
    },
    { key: "changedBy", header: t(S.changedBy), cell: (r) => <span className="muted">{r.changedBy ?? "—"}</span> },
    {
      key: "action",
      header: "",
      cell: (r) => (
        <Button
          variant="secondary"
          size="sm"
          onClick={() => {
            setTarget(r);
            setValue(r.maxValue === null ? "" : String(r.maxValue));
          }}
        >
          {t(S.setCap)}
        </Button>
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp3)" }}>
      <h2 className="panel-h">{t(S.limitsHeading)}</h2>
      <DataTable columns={cols} rows={limits} rowKey={(r) => r.key} caption={t(S.limitsHeading)} />

      <Modal
        open={target !== null}
        onOpenChange={(o) => !o && close()}
        title={t(S.capTitle)}
        footer={
          <>
            <Button variant="ghost" onClick={close}>{t(S.cancel)}</Button>
            <Button variant="primary"
              leadingIcon={<Icon name="check2" />} onClick={confirm} disabled={write.busy}>{t(S.confirm)}</Button>
          </>
        }
      >
        {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}
        <p className="mono">{target?.key}</p>
        <InputField
          type="number"
          min={0}
          label={t(S.capValue)}
          value={value}
          onChange={(e) => setValue(e.currentTarget.value)}
        />
        {/* Allowed, not blocked — tightening a cap on an over-provisioned tenant is legitimate, and the cap
            only ever refuses the NEXT creation. Saying so stops it reading like a mistake. */}
        {belowUsage ? <InlineAlert tone="warn">{t(S.capBelowUsage)}</InlineAlert> : null}
        <TextareaField label={t(S.reason)} value={reason} error={reasonError} onChange={(e) => setReason(e.currentTarget.value)} />
      </Modal>
    </Card>
  );
}
