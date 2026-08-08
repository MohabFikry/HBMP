import { useState } from "react";
import { Button, Card, InlineAlert, InputField, StatusChip, useToast } from "@mersal/design-system";
import type { Localized, ValidityArtefactPolicy, ValidityPolicyView } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Validity periods", ar: "مدد الصلاحية" },
  lede: {
    en: "How long a prescription or an investigation order stays actionable after it is written. This is a "
      + "clinical safety judgement, not a system setting: it is how fast you expect a patient's situation to "
      + "move between the decision and the counter.",
    ar: "المدة التي تظل خلالها الوصفة أو طلب الفحص قابلاً للتنفيذ بعد كتابته. هذا حكم يتعلق بسلامة المريض "
      + "وليس إعداداً تقنياً: فهو تقدير لسرعة تغيّر حالة المريض بين القرار والتنفيذ.",
  },
  appliesTo: {
    en: "A change applies to prescriptions and orders written from now on. Anything already issued keeps the "
      + "expiry it was written with — shortening the window must not strand a patient holding a prescription "
      + "they were told to come back with.",
    ar: "يسري التغيير على الوصفات والطلبات المكتوبة من الآن فصاعداً. أما ما صدر بالفعل فيحتفظ بتاريخ انتهائه "
      + "— فتقصير المدة يجب ألا يترك مريضاً يحمل وصفة طُلب منه العودة بها.",
  },
  expired: {
    en: "When something does expire, the counter can ask you to revalidate it — those requests arrive in the "
      + "approval worklist. A shorter window is not a refusal; it is more of your team's time.",
    ar: "عند انتهاء الصلاحية، يمكن لنقطة الصرف طلب إعادة تفعيلها — وتصل تلك الطلبات إلى قائمة الموافقات. "
      + "المدة الأقصر ليست رفضاً، لكنها تعني وقتاً أكبر من فريقك.",
  },
  artefact: { en: "Applies to", ar: "ينطبق على" },
  days: { en: "Days", ar: "الأيام" },
  save: { en: "Save", ar: "حفظ" },
  bounds: { en: "Between {min} and {max} days.", ar: "بين {min} و{max} يوماً." },
  configured: { en: "Chosen", ar: "مُختار" },
  usingDefault: { en: "Platform default", ar: "الوضع الافتراضي" },
  usingDefaultHint: {
    en: "Nobody has chosen this — it is the platform default of {days} days. Setting it to the same number "
      + "is still a decision, and is recorded as one.",
    ar: "لم يختره أحد — إنه الوضع الافتراضي للمنصّة وهو {days} يوماً. ضبطه على الرقم نفسه يظل قراراً "
      + "ويُسجَّل بهذه الصفة.",
  },
  lastChanged: { en: "Last changed {when}", ar: "آخر تعديل {when}" },
  saved: { en: "{artefact} is now {days} days, for anything written from now on.", ar: "أصبحت مدة {artefact} {days} يوماً لكل ما يُكتب من الآن." },
  saveFailed: { en: "Could not save. Try again.", ar: "تعذّر الحفظ. حاول مرة أخرى." },
  outOfRange: { en: "Between {min} and {max} days.", ar: "بين {min} و{max} يوماً." },
  loadFailed: {
    en: "The validity periods could not be loaded. This is NOT a report that none are set — nothing has "
      + "changed, and prescriptions are still being written with whatever is configured.",
    ar: "تعذّر تحميل مدد الصلاحية. هذا ليس تقريراً بعدم ضبطها — لم يتغيّر شيء، وما زالت الوصفات تُكتب "
      + "بالمدة المضبوطة.",
  },
} satisfies Record<string, Localized>;

const ARTEFACT_LABEL: Record<string, Localized> = {
  Prescription: { en: "Prescriptions", ar: "الوصفات" },
  LabOrder: { en: "Lab orders", ar: "طلبات المختبر" },
  // 29.1 (design 45 §1) — the LABEL is renamed; the KEY is not.
  //
  // `ImagingOrder` is a persisted config vocabulary — `ValidityArtefact.ImagingOrder` keyed on
  // `validity.imaging-order.days`, with a configured row per tenant — and renaming it would rewrite live
  // configuration to chase a label, which is the same trade the IMAGING benefit category was left alone
  // for. What was NOT deliberate was this English string: the Arabic beside it already read الأشعة, so the
  // screen showed a Medical Director two different names for one setting depending on their language.
  ImagingOrder: { en: "Radiology orders", ar: "طلبات الأشعة" },
  ProcedureOrder: { en: "Procedure orders", ar: "طلبات الإجراءات" },
};

/**
 * The Medical Director's control over how long clinical instructions stay actionable.
 *
 * <b>Why this role holds it.</b> How long a prescription remains safe to dispense is a judgement about how
 * fast a patient's condition moves, not a system parameter — and the person who sets it is the one who lives
 * with the consequence, because every extension request a short window produces lands in their own approval
 * queue. The write is gated on <c>AdminPolicies.EditValidityPolicy</c> (Medical Director / Super Admin), NOT
 * on the general config permission held by the people who administer accounts.
 *
 * <b>Four settings, not one.</b> A course of antibiotics and a follow-up scan do not go stale at the same
 * rate. A director who wants them identical can set them identical; the reverse is not possible once they
 * are merged, without asking every tenant what they meant by the single number.
 *
 * <b>What the screen has to say out loud.</b> That a change is not retroactive — otherwise a director
 * shortening the window would expect yesterday's prescriptions to lapse tonight, and would be wrong. And
 * that "10 because nobody has looked at this" is a different state from "10 because we chose 10".
 */
export function ValidityPolicyAdmin() {
  const t = useLoc();
  const api = useApi();
  const policy = useAsync<ValidityPolicyView>(() => api.validityPolicy(), []);

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp5)", marginBlockEnd: "var(--sp4)" }}>
        <p style={{ marginBlockStart: 0 }}>{t(S.lede)}</p>
        <InlineAlert tone="info">{t(S.appliesTo)}</InlineAlert>
        <p className="muted">{t(S.expired)}</p>
      </Card>

      {/* A failed load is never rendered as "none are set" — the periods are still in force server-side
          and prescriptions are still being written against them. */}
      <AsyncSection state={policy} emptyLabel={S.loadFailed}>
        {(view) => (
          <div className="stack">
            {view.items.map((item) => (
              <ArtefactRow key={item.artefact} item={item} view={view} onSaved={policy.reload} />
            ))}
          </div>
        )}
      </AsyncSection>
    </>
  );
}

function ArtefactRow({
  item,
  view,
  onSaved,
}: {
  item: ValidityArtefactPolicy;
  view: ValidityPolicyView;
  onSaved: () => void;
}) {
  const t = useLoc();
  const api = useApi();
  const { toast } = useToast();
  const { date } = useFormat();
  const [days, setDays] = useState(String(item.days));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const label = ARTEFACT_LABEL[item.artefact] ?? { en: item.artefact, ar: item.artefact };
  const parsed = Number(days);
  const inRange = Number.isInteger(parsed) && parsed >= view.minDays && parsed <= view.maxDays;
  const changed = String(item.days) !== days.trim();

  async function save() {
    if (!inRange) {
      setError(t(S.outOfRange).replace("{min}", String(view.minDays)).replace("{max}", String(view.maxDays)));
      return;
    }
    setError(null);
    setBusy(true);
    try {
      await api.setValidityPolicy(item.artefact, parsed);
      toast(t(S.saved).replace("{artefact}", t(label)).replace("{days}", String(parsed)), "ok");
      onSaved();
    } catch {
      toast(t(S.saveFailed), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp4)" }}>
      <div className="result-head">
        <h3 className="section-h" style={{ margin: 0 }}>{t(label)}</h3>
        {/*
          "Set" and "platform default" are different states and only one of them is a decision. A screen
          that showed 10 either way would let a director believe their tenant had chosen it.
        */}
        {item.configured
          ? <StatusChip kind="ok" label={t(S.configured)} />
          : <StatusChip kind="neu" label={t(S.usingDefault)} />}
      </div>

      {!item.configured && (
        <p className="muted">
          {t(S.usingDefaultHint).replace("{days}", String(view.defaultDays))}
        </p>
      )}
      {item.configured && item.updatedAt && (
        <p className="muted tnum">{t(S.lastChanged).replace("{when}", date(item.updatedAt))}</p>
      )}

      <div className="rx-search" style={{ marginBlockEnd: 0 }}>
        <InputField
          label={t(S.days)}
          type="number"
          min={view.minDays}
          max={view.maxDays}
          value={days}
          onChange={(e) => { setDays(e.currentTarget.value); setError(null); }}
        />
        {/* The bounds are the SERVER's, carried in the payload, so the screen and the endpoint cannot come
            to disagree about what a legal validity period is. */}
        <p className="muted tnum">
          {t(S.bounds).replace("{min}", String(view.minDays)).replace("{max}", String(view.maxDays))}
        </p>
        <div className="rx-search-actions">
          <Button variant="primary" loading={busy} disabled={!changed || !inRange || busy} onClick={() => void save()}>
            {t(S.save)}
          </Button>
        </div>
      </div>
      {error && <InlineAlert tone="bad">{error}</InlineAlert>}
    </Card>
  );
}
