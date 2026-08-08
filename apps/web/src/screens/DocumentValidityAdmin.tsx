import { useState } from "react";
import { Button, Card, InlineAlert, InputField, StatusChip, useToast } from "@mersal/design-system";
import type { DocumentValidityItem, DocumentValidityView, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Document validity", ar: "صلاحية المستندات" },
  lede: {
    en: "How long a document is expected to stay current, and how early somebody is told it is about to "
      + "lapse. Both halves land on you: a refugee whose card has expired is stopped at reception, and the "
      + "case that produces arrives in your queue.",
    ar: "المدة التي يُتوقع أن يظل خلالها المستند سارياً، ومتى يُنبَّه أحد إلى قرب انتهائه. وكلا الأمرين يعود "
      + "إليك: فاللاجئ الذي انتهت بطاقته يُوقَف عند الاستقبال، وتصلك الحالة الناتجة في قائمتك.",
  },
  notAnOverride: {
    en: "The renewal period does NOT override an expiry printed on a document. Mersal does not decide when a "
      + "government-issued card lapses — where an expiry was recorded, that is the one that counts. This is "
      + "the cadence used to set a review date when nobody recorded one, and anything derived that way is "
      + "labelled as derived.",
    ar: "لا تلغي مدة التجديد تاريخ الانتهاء المطبوع على المستند. فمرسال لا تحدد متى تنتهي بطاقة صادرة عن "
      + "جهة حكومية — وحيثما سُجِّل تاريخ انتهاء فهو المعتمد. هذه المدة تُستخدم لتحديد موعد مراجعة عندما لا "
      + "يُسجَّل تاريخ، ويُوسم كل ما يُشتق بهذه الطريقة بأنه مشتق.",
  },
  appliesTo: {
    en: "A change applies to documents recorded from now on. Anything already recorded keeps the expiry it "
      + "carries — shortening a period must not lapse a beneficiary's papers that were fine when checked.",
    ar: "يسري التغيير على المستندات المسجَّلة من الآن فصاعداً. أما المسجَّل بالفعل فيحتفظ بتاريخ انتهائه — "
      + "فتقصير المدة يجب ألا يُنهي صلاحية أوراق مستفيد كانت سليمة عند فحصها.",
  },

  identity: { en: "Beneficiary identity", ar: "هوية المستفيد" },
  identityHint: {
    en: "A lapse here stops somebody being SEEN. It warns and is acknowledged at reception; it never blocks "
      + "care on its own — a paperwork lapse is not a reason to turn a patient away.",
    ar: "انتهاء الصلاحية هنا يمنع استقبال الشخص. يظهر تنبيه يُقَر عند الاستقبال، ولا يمنع الرعاية بذاته — "
      + "فتأخر الأوراق ليس سبباً لرد مريض.",
  },
  credential: { en: "Provider credentials", ar: "اعتمادات مقدّم الخدمة" },
  credentialHint: {
    en: "A lapse here stops somebody PRACTISING. A different consequence, reached by a different path.",
    ar: "انتهاء الصلاحية هنا يمنع مزاولة المهنة. نتيجة مختلفة يصل إليها مسار مختلف.",
  },

  kind: { en: "Document", ar: "المستند" },
  days: { en: "Renewal period (days)", ar: "مدة التجديد (أيام)" },
  warn: { en: "Warn at (days before)", ar: "التنبيه قبل (أيام)" },
  warnHint: {
    en: "Comma-separated. Each is a point at which a warning fires — 90,60,30 warns three times, not once.",
    ar: "مفصولة بفواصل. كل رقم نقطة يُطلق عندها تنبيه — 90,60,30 تعني ثلاثة تنبيهات لا واحداً.",
  },
  warnEmpty: {
    en: "Leave at least one. Clearing this would silence an expiring document completely.",
    ar: "أبقِ رقماً واحداً على الأقل. فمسح هذا الحقل يُسكت تنبيهات المستند المنتهي تماماً.",
  },
  warnInvalid: { en: "Whole numbers between {min} and {max}, separated by commas.", ar: "أرقام صحيحة بين {min} و{max} مفصولة بفواصل." },
  bounds: { en: "Between {min} and {max} days.", ar: "بين {min} و{max} يوماً." },
  chosen: { en: "Chosen", ar: "مُختار" },
  usingDefault: { en: "Platform default", ar: "الوضع الافتراضي" },
  usingDefaultHint: {
    en: "Nobody has chosen this — it is the platform default. Setting it to the same number is still a "
      + "decision, and is recorded as one.",
    ar: "لم يخترْه أحد — إنه الوضع الافتراضي. ضبطه على الرقم نفسه يظل قراراً ويُسجَّل كذلك.",
  },
  save: { en: "Save", ar: "حفظ" },
  saved: { en: "Saved.", ar: "تم الحفظ." },
  failed: { en: "Could not save.", ar: "تعذّر الحفظ." },
  updated: { en: "Last changed {when}", ar: "آخر تغيير {when}" },
  never: { en: "Never changed", ar: "لم يُغيَّر" },
  empty: { en: "No document kinds are configured.", ar: "لا توجد أنواع مستندات مهيأة." },
} satisfies Record<string, Localized>;

/** "90,60,30" → [90,60,30]; null when a token is not a whole number. */
function parseWarn(text: string): number[] | null {
  const parts = text.split(",").map((p) => p.trim()).filter((p) => p !== "");
  if (parts.length === 0) return [];
  const nums = parts.map((p) => (/^\d+$/.test(p) ? Number(p) : NaN));
  return nums.some(Number.isNaN) ? null : nums;
}

/**
 * How long a document is good for, and how early its lapse is warned about (ADR-0035 §6).
 *
 * <b>Why the supervisor owns this.</b> The same argument as the prescription validity screen beside it: a
 * refugee whose card lapsed is stopped at reception, and the case that produces lands on this desk. The
 * person who absorbs the consequence sets the number. The warning thresholds in particular were a compiled-in
 * constant — `PractitionerLicence.WarningDays = [90, 60, 30]` — so the number a supervisor most obviously
 * owns was the one they could not touch.
 *
 * <b>Why the two families are shown apart.</b> An identity document and a provider credential fail
 * differently: one stops a person being SEEN, the other stops a person PRACTISING. Listing them in one table
 * would put two different consequences under one heading, and the supervisor's judgement about a refugee
 * card is not the same judgement they make about a licence.
 */
export function DocumentValidityAdmin() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<DocumentValidityView>(() => api.adminDocumentValidity(), []);

  return (
    <>
      <PageHeader title={t(S.title)} />

      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <p className="muted">{t(S.lede)}</p>
        {/* The most important sentence on the page: this is not the platform deciding when somebody's papers
            expire. Stated as an alert rather than help text because getting it wrong is a fact invented about
            a refugee's documents. */}
        <InlineAlert tone="info">{t(S.notAnOverride)}</InlineAlert>
        <p className="muted" style={{ marginBlockStart: "var(--sp3)" }}>{t(S.appliesTo)}</p>
      </Card>

      <AsyncSection state={state} isEmpty={(d) => d.items.length === 0} emptyLabel={S.empty}>
        {(view) => (
          <>
            <Section
              heading={t(S.identity)} hint={t(S.identityHint)}
              items={view.items.filter((i) => i.identity)} view={view} onSaved={state.reload}
            />
            <Section
              heading={t(S.credential)} hint={t(S.credentialHint)}
              items={view.items.filter((i) => !i.identity)} view={view} onSaved={state.reload}
            />
          </>
        )}
      </AsyncSection>
    </>
  );
}

function Section({
  heading, hint, items, view, onSaved,
}: {
  heading: string;
  hint: string;
  items: DocumentValidityItem[];
  view: DocumentValidityView;
  onSaved: () => void | Promise<unknown>;
}) {
  if (items.length === 0) return null;
  return (
    <Card as="section" style={{ padding: "var(--sp5)", marginBlockStart: "var(--sp4)" }}>
      <h2 className="section-h">{heading}</h2>
      <p className="muted">{hint}</p>
      <div className="stack" style={{ marginBlockStart: "var(--sp4)" }}>
        {items.map((item) => (
          <KindRow key={item.kind} item={item} view={view} onSaved={onSaved} />
        ))}
      </div>
    </Card>
  );
}

function KindRow({
  item, view, onSaved,
}: {
  item: DocumentValidityItem;
  view: DocumentValidityView;
  onSaved: () => void | Promise<unknown>;
}) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const { toast } = useToast();

  const [days, setDays] = useState(String(item.days));
  const [warn, setWarn] = useState(item.warnDays.join(","));
  const [busy, setBusy] = useState(false);

  const daysNum = Number(days);
  const daysBad = !/^\d+$/.test(days.trim()) || daysNum < view.minDays || daysNum > view.maxDays;
  const parsedWarn = parseWarn(warn);
  // Two DIFFERENT wrongs with two different messages: an empty list would silence the document entirely,
  // while a bad token is a typo. One error string for both would tell a supervisor to fix the wrong thing.
  const warnEmpty = parsedWarn !== null && parsedWarn.length === 0;
  const warnBad = parsedWarn === null
    || (parsedWarn.length > 0 && parsedWarn.some((n) => n < view.minDays || n > view.maxDays));

  async function save() {
    if (daysBad || warnEmpty || warnBad) return;
    setBusy(true);
    try {
      // Only what changed. Sending both every time would make an untouched threshold list a fresh write with
      // this supervisor's name on it — a decision they did not make.
      await api.adminSetDocumentValidity({
        kind: item.kind,
        ...(daysNum === item.days ? {} : { days: daysNum }),
        ...(warn === item.warnDays.join(",") ? {} : { warnDays: parsedWarn ?? undefined }),
      });
      toast(t(S.saved), "ok");
      await onSaved();
    } catch {
      toast(t(S.failed), "bad");
    } finally {
      setBusy(false);
    }
  }

  const dirty = daysNum !== item.days || warn !== item.warnDays.join(",");

  return (
    <div className="dv-row">
      <div className="dv-kind">
        <strong>{item.kind}</strong>
        {/* "365 because we chose 365" and "365 because nobody has looked" are different states, and only one
            of them is a decision. The chip says which. */}
        <StatusChip
          kind={item.configured ? "ok" : "neu"}
          label={t(item.configured ? S.chosen : S.usingDefault)}
        />
        <span className="muted dv-when">
          {item.updatedAt ? t(S.updated).replace("{when}", fmt.date(item.updatedAt)) : t(S.never)}
        </span>
        {!item.configured && <p className="muted dv-hint">{t(S.usingDefaultHint)}</p>}
      </div>

      <InputField
        label={t(S.days)}
        inputMode="numeric"
        value={days}
        help={t(S.bounds).replace("{min}", String(view.minDays)).replace("{max}", String(view.maxDays))}
        error={daysBad ? t(S.bounds).replace("{min}", String(view.minDays)).replace("{max}", String(view.maxDays)) : undefined}
        onChange={(e) => setDays(e.currentTarget.value)}
      />

      <InputField
        label={t(S.warn)}
        value={warn}
        help={t(S.warnHint)}
        error={
          warnEmpty ? t(S.warnEmpty)
            : warnBad ? t(S.warnInvalid).replace("{min}", String(view.minDays)).replace("{max}", String(view.maxDays))
              : undefined
        }
        onChange={(e) => setWarn(e.currentTarget.value)}
      />

      <Button
        variant="primary"
        loading={busy}
        disabled={daysBad || warnEmpty || warnBad || !dirty}
        onClick={() => void save()}
      >
        {t(S.save)}
      </Button>
    </div>
  );
}
