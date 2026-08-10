import { useMemo, useState } from "react";
import {
  Button, Card, DataTable, Icon, InlineAlert, InputField, SegmentedControl, SelectField, StatusChip,
  TextareaField, useToast,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  ApprovalRule, ApprovalRuleList, AutoDecisionSwitch, Localized, RulePredicate,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Approvals engine", ar: "محرك الموافقات" },
  lede: {
    en: "Rules that decide which queue a request lands on and how long the reviewer has. They change WHO "
      + "decides and BY WHEN — never what is decided. Nothing here can approve or refuse anything.",
    ar: "قواعد تحدد قائمة الانتظار التي يصل إليها الطلب والمدة المتاحة للمراجع. تغيّر من يقرر ومتى — لا ما "
      + "يُقرَّر. ولا شيء هنا يمكنه الموافقة أو الرفض.",
  },
  appendOnly: {
    en: "Publishing a change closes the current version and opens a new one. A request routed last Tuesday "
      + "stays explainable against the rules in force last Tuesday — which is why superseded versions are "
      + "still listed, and why there is no delete.",
    ar: "نشر أي تغيير يُغلق الإصدار الحالي ويفتح آخر. فالطلب الذي وُجِّه الثلاثاء الماضي يظل قابلاً للتفسير "
      + "وفق القواعد السارية حينها — ولهذا تبقى الإصدارات السابقة معروضة، ولا يوجد حذف.",
  },
  fallback: {
    en: "A request matching no rule goes to \"{queue}\". Routing must never strand work: a request nobody can "
      + "see is worse than one routed imperfectly.",
    ar: "الطلب الذي لا تنطبق عليه أي قاعدة يذهب إلى \"{queue}\". فالتوجيه يجب ألا يترك عملاً معلقاً: طلب لا "
      + "يراه أحد أسوأ من طلب وُجِّه بشكل غير مثالي.",
  },

  routing: { en: "Routing", ar: "التوجيه" },
  sla: { en: "Time limits", ar: "المهل الزمنية" },
  preauth: { en: "Pre-approval", ar: "الموافقة المسبقة" },
  autoApprove: { en: "Auto-approval", ar: "الموافقة التلقائية" },
  autoHint: {
    en: "Requests approved WITHOUT a human, within a ceiling. There is no auto-rejection and there will not "
      + "be: a wrong auto-approval costs the payer money and a human reviews the claim later, while a wrong "
      + "auto-rejection denies care to somebody with nobody having looked.",
    ar: "طلبات تُعتمد دون تدخل بشري ضمن حد أقصى. ولا يوجد رفض تلقائي ولن يوجد: فالموافقة التلقائية الخاطئة "
      + "تكلّف الممول مالاً ويراجع إنسان المطالبة لاحقاً، أما الرفض التلقائي الخاطئ فيحرم شخصاً من الرعاية "
      + "دون أن ينظر فيه أحد.",
  },
  ceiling: { en: "Approve up to (EGP)", ar: "الاعتماد حتى (ج.م)" },
  ceilingHint: {
    en: "At most {max}. The platform maximum binds whatever a rule claims for itself — otherwise \"bounded\" "
      + "would mean bounded by whatever the last person to edit it typed.",
    ar: "بحد أقصى {max}. الحد الأقصى للمنصّة يقيّد ما تدّعيه أي قاعدة لنفسها — وإلا لأصبح \"المحدود\" محدوداً "
      + "بما كتبه آخر من عدّلها.",
  },
  ceilingBad: { en: "Between 1 and {max} EGP.", ar: "بين 1 و{max} ج.م." },
  autoCatchAll: {
    en: "An auto-approval rule with no conditions would approve ANY request under the ceiling without a "
      + "human. Narrow it — by category, service code or provider.",
    ar: "قاعدة موافقة تلقائية بلا شروط ستعتمد أي طلب دون الحد الأقصى بلا تدخل بشري. ضيّق نطاقها — بالفئة أو "
      + "الرمز أو مقدم الخدمة.",
  },

  // ---- the kill switch ----
  switchTitle: { en: "Auto-decision", ar: "القرار التلقائي" },
  switchOn: { en: "ON — some requests are approved without a human", ar: "مُفعّل — تُعتمد بعض الطلبات دون تدخل بشري" },
  switchOff: { en: "OFF — every request waits for a person", ar: "مُعطّل — كل طلب ينتظر شخصاً" },
  switchHint: {
    en: "One switch, and it does not edit any rule. Turning it off stops every auto-approval immediately and "
      + "leaves the rules exactly where they are, so you can investigate afterwards rather than first.",
    ar: "مفتاح واحد لا يعدّل أي قاعدة. إيقافه يوقف كل موافقة تلقائية فوراً ويترك القواعد كما هي، فتتحقق "
      + "لاحقاً بدل أن تضطر للتحقق أولاً.",
  },
  turnOn: { en: "Turn on", ar: "تفعيل" },
  turnOff: { en: "Turn off", ar: "إيقاف" },
  switchReason: { en: "Why", ar: "السبب" },
  switchReasonHint: {
    en: "Required either way. Turning it on is a decision somebody owns; turning it off in a hurry is one "
      + "somebody has to explain the following morning.",
    ar: "مطلوب في الحالتين. تفعيله قرار يتحمله شخص، وإيقافه على عجل قرار سيُطلب تفسيره في الصباح التالي.",
  },
  switchReasonMissing: { en: "State why. It is recorded.", ar: "اذكر السبب. يُسجَّل." },
  lastChanged: { en: "Last changed by {who}", ar: "آخر تغيير بواسطة {who}" },
  routingHint: { en: "Which desk a request lands on.", ar: "أي مكتب يصل إليه الطلب." },
  slaHint: { en: "How long the reviewer has once they pick it up.", ar: "المدة المتاحة للمراجع بعد استلامه." },
  preauthHint: {
    en: "Care that ALSO needs a decision before it happens. These rules only ever ADD a requirement — the "
      + "plan's own terms are contractual and nothing here can switch one off.",
    ar: "رعاية تحتاج أيضاً إلى قرار قبل تقديمها. هذه القواعد تضيف اشتراطاً فقط — فشروط الخطة تعاقدية ولا "
      + "شيء هنا يمكنه إلغاء أي منها.",
  },
  category: { en: "Benefit category", ar: "فئة المنفعة" },
  amountAtLeast: { en: "Amount at least (EGP)", ar: "المبلغ لا يقل عن (ج.م)" },
  amountHint: {
    en: "Leave blank for any amount. A request whose cost is UNKNOWN does not clear this — an absent figure "
      + "is not a small one.",
    ar: "اتركه فارغاً لأي مبلغ. والطلب مجهول التكلفة لا يستوفي هذا الشرط — فغياب الرقم لا يعني أنه صغير.",
  },
  reason: { en: "Reason shown to the provider", ar: "السبب الظاهر لمقدم الخدمة" },
  reasonHint: {
    en: "Required. It is shown to the person this stops. \"Authorization is required\" with no account of why "
      + "is how a gate becomes something people work around.",
    ar: "مطلوب. يُعرض على من يوقفه هذا الشرط. فعبارة \"يلزم تفويض\" دون بيان السبب هي ما يجعل الضوابط شيئاً "
      + "يلتف الناس حوله.",
  },
  reasonMissing: { en: "Say why. The provider sees this.", ar: "اذكر السبب. سيراه مقدم الخدمة." },
  preauthCatchAll: {
    en: "A pre-approval rule with no conditions would require a decision for EVERY act of care on the "
      + "platform. Narrow it — by category, service code, amount or provider.",
    ar: "قاعدة موافقة مسبقة بلا شروط ستوجب قراراً لكل خدمة على المنصّة. ضيّق نطاقها — بالفئة أو الرمز أو "
      + "المبلغ أو مقدم الخدمة.",
  },

  order: { en: "Order", ar: "الترتيب" },
  when: { en: "When", ar: "الشرط" },
  then: { en: "Then", ar: "الإجراء" },
  state: { en: "State", ar: "الحالة" },
  why: { en: "Why", ar: "السبب" },
  live: { en: "Live", ar: "سارية" },
  disabled: { en: "Disabled", ar: "معطّلة" },
  superseded: { en: "Superseded", ar: "مُستبدلة" },
  anyRequest: { en: "Any request", ar: "أي طلب" },
  empty: { en: "No rules yet — everything goes to the default queue.", ar: "لا توجد قواعد بعد — كل شيء يذهب إلى القائمة الافتراضية." },

  add: { en: "New rule", ar: "قاعدة جديدة" },
  editing: { en: "New rule", ar: "قاعدة جديدة" },
  priorityLabel: { en: "Order (lower runs first)", ar: "الترتيب (الأقل ينفَّذ أولاً)" },
  priorityHint: {
    en: "First match wins. Ties resolve the same way every time, so a request is never routed two ways on two "
      + "days with nothing changed.",
    ar: "أول تطابق يفوز. وتُحلّ الحالات المتساوية بالطريقة نفسها دائماً، فلا يُوجَّه الطلب باتجاهين مختلفين "
      + "في يومين دون أي تغيير.",
  },
  matchPriority: { en: "Request priority", ar: "أولوية الطلب" },
  matchSource: { en: "Comes from", ar: "مصدره" },
  any: { en: "Any", ar: "الكل" },
  queue: { en: "Send to", ar: "يُرسل إلى" },
  hours: { en: "Hours allowed", ar: "الساعات المتاحة" },
  hoursHint: { en: "Between 1 and 720. Zero would breach on arrival.", ar: "بين 1 و720. الصفر يعني تجاوزاً فور الوصول." },
  hoursBad: { en: "Between 1 and 720 hours.", ar: "بين 1 و720 ساعة." },
  rationaleLabel: { en: "Why this rule", ar: "سبب هذه القاعدة" },
  rationaleHint: {
    en: "Required. This is what somebody reads when asking why work went where it went.",
    ar: "مطلوب. هذا ما سيقرأه من يسأل لماذا ذهب العمل حيث ذهب.",
  },
  rationaleMissing: { en: "State why. It is recorded with the rule.", ar: "اذكر السبب. يُسجَّل مع القاعدة." },
  catchAllWarn: {
    en: "This rule matches EVERY request. Put it last, or it will take everything before the rules below it "
      + "get a chance.",
    ar: "تنطبق هذه القاعدة على كل الطلبات. ضعها في النهاية، وإلا استحوذت على كل شيء قبل أن تعمل القواعد التي "
      + "تليها.",
  },
  save: { en: "Publish", ar: "نشر" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  saved: { en: "Published as version {n}.", ar: "نُشرت كإصدار {n}." },
  failed: { en: "Could not publish.", ar: "تعذّر النشر." },
} satisfies Record<string, Localized>;

/**
 * The platform ceiling, mirrored from `AutoApproval.HardMaximumEgp`.
 *
 * <p>The server is the authority and refuses anything above it regardless; this is here so the field can say
 * the bound before somebody types past it, rather than after.</p>
 */
const HARD_MAX = 5000;

/** A predicate rendered as the sentence a supervisor would say. */
function describe(json: string, t: (l: Localized) => string): string {
  let p: RulePredicate;
  try { p = JSON.parse(json) as RulePredicate; } catch { return json; }
  const parts: string[] = [];
  if (p.priority) parts.push(`${t(S.matchPriority)} = ${p.priority}`);
  if (p.source) parts.push(`${t(S.matchSource)} = ${p.source}`);
  if (p.kind) parts.push(`kind = ${p.kind}`);
  if (p.serviceCodes?.length) parts.push(`code ∈ ${p.serviceCodes.join(", ")}`);
  if (p.requestingProviderId) parts.push(`provider = ${p.requestingProviderId}`);
  if (p.benefitCategory) parts.push(`${t(S.category)} = ${p.benefitCategory}`);
  if (p.amountAtLeast != null) parts.push(`≥ ${p.amountAtLeast}`);
  return parts.length === 0 ? t(S.anyRequest) : parts.join(" · ");
}

function describeAction(json: string): string {
  try {
    const a = JSON.parse(json) as { queue?: string; hours?: number; reason?: string; maxAmountEgp?: number };
    if (a.queue) return `→ ${a.queue}`;
    if (a.hours) return `${a.hours}h`;
    // A preauth rule's whole action IS its reason — the thing the provider is shown when stopped.
    if (a.maxAmountEgp) return `≤ ${a.maxAmountEgp} — ${a.reason ?? ""}`;
    if (a.reason) return a.reason;
    return json;
  } catch { return json; }
}

/**
 * Authoring the approvals engine's routing and SLA rules (ADR-0035 §5.1/§5.4).
 *
 * <b>Why these two families first.</b> They change WHO decides and BY WHEN, never WHAT is decided — so the
 * rule infrastructure gets proved on a family where the worst outcome is work arriving on the wrong desk,
 * not a benefit decision made without a human. Pre-auth triggers and auto-approval build on this.
 *
 * <b>Why superseded rules stay on screen.</b> The question a supervisor actually asks is "why did this go
 * there last week", and today's rules cannot answer it. Effective dating is only useful if the closed windows
 * are visible.
 *
 * <b>Why a catch-all is warned about rather than refused.</b> A rule matching every request is legitimate —
 * it is how you give unmatched work a home — but placed above a specific rule it silently swallows
 * everything, and the specific rule then looks live while doing nothing.
 */
export function ApprovalEngineAdmin() {
  const api = useApi();
  const t = useLoc();
  const [family, setFamily] = useState<"Routing" | "Sla" | "Preauth" | "AutoApprove">("Routing");
  const state = useAsync<ApprovalRuleList>(() => api.approvalRules(), []);
  const [adding, setAdding] = useState(false);

  return (
    <>
      <PageHeader title={t(S.title)} />

      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <p className="muted">{t(S.lede)}</p>
        <InlineAlert tone="info">{t(S.appendOnly)}</InlineAlert>
      </Card>

      <KillSwitch />

      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.empty}>
        {(view) => (
          <>
            <Card as="section" style={{ padding: "var(--sp3)", marginBlockStart: "var(--sp4)" }}>
              <div className="rx-card-head">
                <SegmentedControl
                  aria-label={t(S.title)}
                  value={family}
                  onChange={setFamily}
                  segments={[
                    { value: "Routing", label: t(S.routing) },
                    { value: "Sla", label: t(S.sla) },
                    { value: "Preauth", label: t(S.preauth) },
                    { value: "AutoApprove", label: t(S.autoApprove) },
                  ]}
                />
                <Button variant="primary" size="sm" leadingIcon={<Icon name="plus" />} onClick={() => setAdding(true)}>
                  {t(S.add)}
                </Button>
              </div>
              <p className="muted">{t(family === "Routing" ? S.routingHint : family === "Sla" ? S.slaHint : family === "Preauth" ? S.preauthHint : S.autoHint)}</p>
              {family === "Routing" && (
                <InlineAlert tone="info">
                  {t(S.fallback).replace("{queue}", view.defaultQueue)}
                </InlineAlert>
              )}
              <RuleTable rules={view.rules.filter((r) => r.family === family)} />
            </Card>

            {adding && (
              <RuleEditor
                family={family}
                queues={view.queues}
                onCancel={() => setAdding(false)}
                onSaved={async () => { setAdding(false); await state.reload(); }}
              />
            )}
          </>
        )}
      </AsyncSection>
    </>
  );
}

/**
 * The kill switch.
 *
 * <b>Its own card, above the rules, and it edits nothing.</b> The control somebody reaches for at 02:00
 * because a rule is misbehaving must be one action away — a switch you can only reach by editing the thing
 * that is misbehaving is not a kill switch. Turning it off stops every auto-approval immediately and leaves
 * the rules exactly where they are, so the investigation happens afterwards rather than first.
 */
function KillSwitch() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const { toast } = useToast();
  const state = useAsync<AutoDecisionSwitch>(() => api.autoDecisionSwitch(), []);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [touched, setTouched] = useState(false);

  async function flip(next: boolean) {
    setTouched(true);
    if (reason.trim() === "") return;
    setBusy(true);
    try {
      await api.setAutoDecision({ enabled: next, reason: reason.trim() });
      setReason("");
      setTouched(false);
      await state.reload();
    } catch {
      toast(t(S.failed), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", marginBlockStart: "var(--sp4)" }}>
      <div className="rx-card-head">
        <h2 className="section-h">{t(S.switchTitle)}</h2>
        <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.empty}>
          {(sw) => (
            /* Word AND hue. A switch whose state is carried by colour alone is the one control on the page
               where a misread costs money. */
            <StatusChip kind={sw.enabled ? "warn" : "ok"} label={t(sw.enabled ? S.switchOn : S.switchOff)} />
          )}
        </AsyncSection>
      </div>

      <p className="muted">{t(S.switchHint)}</p>

      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.empty}>
        {(sw) => (
          <div className="stack" style={{ marginBlockStart: "var(--sp3)" }}>
            <p className="muted">
              {sw.reason}
              {sw.updatedBy && sw.updatedAt
                ? ` · ${t(S.lastChanged).replace("{who}", sw.updatedBy)} · ${fmt.date(sw.updatedAt)}`
                : ""}
            </p>
            <InputField
              label={t(S.switchReason)}
              help={t(S.switchReasonHint)}
              value={reason}
              error={touched && reason.trim() === "" ? t(S.switchReasonMissing) : undefined}
              onChange={(e) => setReason(e.currentTarget.value)}
            />
            <div className="pol-editor-actions">
              <Button
                variant={sw.enabled ? "danger" : "primary"}
                loading={busy}
                onClick={() => void flip(!sw.enabled)}
              >
                {t(sw.enabled ? S.turnOff : S.turnOn)}
              </Button>
            </div>
          </div>
        )}
      </AsyncSection>
    </Card>
  );
}

function RuleTable({ rules }: { rules: ApprovalRule[] }) {
  const t = useLoc();
  const fmt = useFormat();

  const cols: Column<ApprovalRule>[] = [
    { key: "order", header: t(S.order), cell: (r) => r.priority, numeric: true, sortable: true, sortValue: (r) => r.priority },
    { key: "when", header: t(S.when), cell: (r) => <span className="mono">{describe(r.predicate, t)}</span> },
    { key: "then", header: t(S.then), cell: (r) => <strong>{describeAction(r.action)}</strong> },
    {
      key: "state", header: t(S.state),
      // Three states, and they are genuinely different: live, deliberately switched off, and replaced by a
      // newer version. Collapsing the last two would hide whether a rule was retired or superseded.
      cell: (r) =>
        r.effectiveTo
          ? <StatusChip kind="neu" label={`${t(S.superseded)} · ${fmt.date(r.effectiveTo)}`} />
          : r.enabled
            ? <StatusChip kind="ok" label={t(S.live)} />
            : <StatusChip kind="warn" label={t(S.disabled)} />,
    },
    { key: "why", header: t(S.why), cell: (r) => r.rationale, sortable: true, sortValue: (r) => r.rationale },
  ];

  return (
    <DataTable
      columns={cols}
      rows={rules}
      rowKey={(r) => r.id}
      caption={t(S.title)}
      emptyLabel={t(S.empty)}
    />
  );
}

function RuleEditor({
  family, queues, onCancel, onSaved,
}: {
  family: "Routing" | "Sla" | "Preauth" | "AutoApprove";
  queues: string[];
  onCancel: () => void;
  onSaved: () => void | Promise<unknown>;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();

  const [priority, setPriority] = useState("50");
  const [matchPriority, setMatchPriority] = useState("");
  const [matchSource, setMatchSource] = useState("");
  const [queue, setQueue] = useState(queues[0] ?? "default");
  const [hours, setHours] = useState("24");
  const [category, setCategory] = useState("");
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const [ceiling, setCeiling] = useState("500");
  const [rationale, setRationale] = useState("");
  const [busy, setBusy] = useState(false);
  const [touched, setTouched] = useState(false);

  const predicate = useMemo<RulePredicate>(() => ({
    priority: (matchPriority || null) as RulePredicate["priority"],
    source: (matchSource || null) as RulePredicate["source"],
    benefitCategory: category || null,
    amountAtLeast: amount.trim() === "" ? null : Number(amount),
  }), [matchPriority, matchSource, category, amount]);

  const isCatchAll = !matchPriority && !matchSource && !category && amount.trim() === "";
  const hoursNum = Number(hours);
  const hoursBad = family === "Sla" && (!/^\d+$/.test(hours.trim()) || hoursNum < 1 || hoursNum > 720);
  const rationaleMissing = touched && rationale.trim() === "";
  const reasonMissing = (family === "Preauth" || family === "AutoApprove") && touched && reason.trim() === "";
  // Refused, not warned. For routing a catch-all gives unmatched work a home; for pre-approval it would put
  // every act of care on the platform behind a decision, which is a service outage with a benefit rationale.
  const preauthCatchAll = family === "Preauth" && isCatchAll;
  // The worst rule anybody could write: approve anything under the ceiling, with no human. Refused, like the
  // pre-approval catch-all and for a sharper version of the same reason.
  const autoCatchAll = family === "AutoApprove" && isCatchAll;
  const ceilingNum = Number(ceiling);
  const ceilingBad = family === "AutoApprove"
    && (!/^\d+$/.test(ceiling.trim()) || ceilingNum < 1 || ceilingNum > HARD_MAX);

  async function save() {
    setTouched(true);
    if (rationale.trim() === "" || hoursBad || preauthCatchAll || autoCatchAll || ceilingBad) return;
    if ((family === "Preauth" || family === "AutoApprove") && reason.trim() === "") return;
    setBusy(true);
    try {
      const r = await api.saveApprovalRule({
        family,
        priority: Number(priority) || 0,
        predicate,
        action: family === "Routing" ? { queue }
          : family === "Sla" ? { hours: hoursNum }
            : family === "Preauth" ? { reason: reason.trim() }
              : { maxAmountEgp: ceilingNum, reason: reason.trim() },
        rationale: rationale.trim(),
        enabled: true,
      });
      toast(t(S.saved).replace("{n}", String(r.versionNo)), "ok");
      await onSaved();
    } catch {
      toast(t(S.failed), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", marginBlockStart: "var(--sp4)" }}>
      <h2 className="section-h">{t(S.editing)}</h2>

      <div className="stack" style={{ marginBlockStart: "var(--sp3)" }}>
        <InputField
          label={t(S.priorityLabel)}
          help={t(S.priorityHint)}
          inputMode="numeric"
          value={priority}
          onChange={(e) => setPriority(e.currentTarget.value)}
        />

        <SelectField
          label={t(S.matchPriority)}
          value={matchPriority}
          onChange={setMatchPriority}
          options={[
            { value: "", label: t(S.any) },
            { value: "Routine", label: "Routine" },
            { value: "Urgent", label: "Urgent" },
            { value: "Emergency", label: "Emergency" },
          ]}
        />

        <SelectField
          label={t(S.matchSource)}
          value={matchSource}
          onChange={setMatchSource}
          options={[
            { value: "", label: t(S.any) },
            { value: "OrderLine", label: "OrderLine" },
            { value: "Prescription", label: "Prescription" },
            { value: "Manual", label: "Manual" },
          ]}
        />

        {family === "AutoApprove" && (
          <>
            <SelectField
              label={t(S.category)}
              value={category}
              onChange={setCategory}
              options={[
                { value: "", label: t(S.any) },
                { value: "CONSULT", label: "CONSULT" },
                { value: "LAB", label: "LAB" },
                { value: "IMAGING", label: "IMAGING" },
                { value: "PHARMACY", label: "PHARMACY" },
                { value: "REFERRAL", label: "REFERRAL" },
              ]}
            />
            <InputField
              label={t(S.ceiling)}
              help={t(S.ceilingHint).replace("{max}", String(HARD_MAX))}
              inputMode="numeric"
              value={ceiling}
              error={ceilingBad ? t(S.ceilingBad).replace("{max}", String(HARD_MAX)) : undefined}
              onChange={(e) => setCeiling(e.currentTarget.value)}
            />
            <TextareaField
              label={t(S.reason)}
              help={t(S.reasonHint)}
              rows={2}
              value={reason}
              error={reasonMissing ? t(S.reasonMissing) : undefined}
              onChange={(e) => setReason(e.currentTarget.value)}
            />
          </>
        )}

        {family === "Preauth" && (
          <>
            <SelectField
              label={t(S.category)}
              value={category}
              onChange={setCategory}
              options={[
                { value: "", label: t(S.any) },
                { value: "CONSULT", label: "CONSULT" },
                { value: "LAB", label: "LAB" },
                { value: "IMAGING", label: "IMAGING" },
                { value: "PHARMACY", label: "PHARMACY" },
                { value: "REFERRAL", label: "REFERRAL" },
              ]}
            />
            <InputField
              label={t(S.amountAtLeast)}
              help={t(S.amountHint)}
              inputMode="numeric"
              value={amount}
              onChange={(e) => setAmount(e.currentTarget.value)}
            />
            <TextareaField
              label={t(S.reason)}
              help={t(S.reasonHint)}
              rows={2}
              value={reason}
              error={reasonMissing ? t(S.reasonMissing) : undefined}
              onChange={(e) => setReason(e.currentTarget.value)}
            />
          </>
        )}

        {family === "Routing" ? (
          <SelectField
            label={t(S.queue)}
            value={queue}
            onChange={setQueue}
            // Only declared queues. A free-text field would let a typo route work somewhere nobody watches,
            // and the symptom is a quiet queue rather than an error.
            options={queues.map((q) => ({ value: q, label: q }))}
          />
        ) : family === "Sla" ? (
          <InputField
            label={t(S.hours)}
            help={t(S.hoursHint)}
            inputMode="numeric"
            value={hours}
            error={hoursBad ? t(S.hoursBad) : undefined}
            onChange={(e) => setHours(e.currentTarget.value)}
          />
        ) : null}

        <TextareaField
          label={t(S.rationaleLabel)}
          help={t(S.rationaleHint)}
          rows={2}
          value={rationale}
          error={rationaleMissing ? t(S.rationaleMissing) : undefined}
          onChange={(e) => setRationale(e.currentTarget.value)}
        />

        {/* Warned, not refused: a catch-all is how you give unmatched work a home. Placed above a specific
            rule it silently swallows everything, and the specific rule then looks live while doing nothing. */}
        {isCatchAll && (
          <InlineAlert tone={preauthCatchAll || autoCatchAll ? "bad" : "warn"}>
            {t(autoCatchAll ? S.autoCatchAll : preauthCatchAll ? S.preauthCatchAll : S.catchAllWarn)}
          </InlineAlert>
        )}
      </div>

      <div className="pol-editor-actions">
        <Button variant="ghost" onClick={onCancel}>{t(S.cancel)}</Button>
        <Button
          variant="primary"
          loading={busy}
          disabled={hoursBad || preauthCatchAll || autoCatchAll || ceilingBad}
          leadingIcon={<Icon name="check2" />}
          onClick={() => void save()}
        >
          {t(S.save)}
        </Button>
      </div>
    </Card>
  );
}
