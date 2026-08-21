import { useState } from "react";
import { Link, useLocation } from "react-router-dom";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, ComboboxField, Icon, InlineAlert, InputField, StatusChip } from "@mersal/design-system";
import type { EligibilityResult, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { ApiError } from "../api/http";
import { useAuth } from "../auth/AuthProvider";
import { hasPermission, permissionsForRole } from "../authz/permissions";
import type { Role } from "../authz/permissions";
import { PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Eligibility Check", ar: "التحقق من الأهلية" },

  // ---- 33.9 — two fields, both required ----
  //
  // This was ONE box taking a card number, an ID, or any fragment of a name, and the screen then checked the
  // first hit it came back with. "Ahmed" matched every Ahmed on the platform and one of them was shown, with
  // nothing to say there had been others — so the plan, the remaining cap and the visit verdict on screen
  // could belong to a person nobody had picked.
  // The labels carry the instruction. Three paragraphs of help under three fields is a form nobody reads —
  // and the two rules that MATTER (an identifier must match in full, a name fragment needs two letters) are
  // enforced by the service and reported as refusals with their own wording, so the help text was restating
  // in advance what the screen says precisely at the moment it applies.
  fieldId: { en: "Card, member or ID number", ar: "رقم البطاقة أو العضوية أو الهوية" },
  fieldName: { en: "Part of their name", ar: "جزء من اسمهم" },
  check: { en: "Check eligibility", ar: "تحقق من الأهلية" },
  // The idle card used to repeat the instruction already sitting on the field above it. What it says now is
  // the thing the field cannot: what the check ANSWERS, so an operator knows before running it whether this
  // is the screen that settles the question in front of them.
  idle: {
    en: "The number and the name must agree before anything is shown. A check then returns the plan, benefit "
      + "band, annual cap remaining, and whether a visit is allowed today. Name the benefit category and it "
      + "also returns the copay for it.",
    ar: "يجب أن يتطابق الرقم مع الاسم قبل عرض أي شيء. ثم يعرض التحقق الخطة وفئة المنفعة والمتبقي من الحد "
      + "السنوي، وما إذا كانت الزيارة مسموحة اليوم. وإذا حدّدت فئة المنفعة فسيعرض أيضاً المساهمة الخاصة بها.",
  },
  loading: { en: "Checking…", ar: "جارٍ التحقق…" },
  error: { en: "Couldn't check eligibility. Try again.", ar: "تعذّر التحقق من الأهلية. حاول مجدداً." },
  retry: { en: "Try again", ar: "حاول مجدداً" },
  noneTitle: { en: "No matching beneficiary", ar: "لا يوجد مستفيد مطابق" },
  noneBody: {
    en: "Check the card or ID number for a mis-read digit. If it is right, this person is not registered yet — register them before the visit.",
    ar: "تحقّق من رقم البطاقة أو الهوية بحثًا عن رقم مقروء خطأ. وإذا كان صحيحًا فهذا الشخص غير مسجَّل بعد — سجّله قبل الزيارة.",
  },

  // ---- 33.9 — the two refusals, told apart because they lead to different actions ----
  //
  // The screen deliberately does NOT show the name on file. The service does not send it, and that is the
  // point: an answer of "no, that card belongs to Amal Hassan" would give the name behind any card number to
  // whoever is holding one.
  mismatchTitle: { en: "That name does not match this number", ar: "هذا الاسم لا يطابق هذا الرقم" },
  mismatchBody: {
    en: "The number is on file and the name given does not belong to it. Ask them to say their name again, "
      + "and check you are reading the right card. Do not continue on this record — the coverage behind it is "
      + "somebody else's.",
    ar: "الرقم مسجَّل والاسم المُدخل لا يخصّه. اطلب منهم قول الاسم مرة أخرى، وتأكّد أنك تقرأ البطاقة الصحيحة. "
      + "لا تتابع على هذا السجل — فالتغطية خلفه تخصّ شخصاً آخر.",
  },
  shortTitle: { en: "Type more of the name", ar: "أدخل المزيد من الاسم" },
  shortBody: {
    en: "Two letters or more. A single letter matches too many people to confirm anything.",
    ar: "حرفان أو أكثر. الحرف الواحد يطابق عدداً كبيراً من الأشخاص ولا يؤكّد شيئاً.",
  },
  coverage: { en: "Coverage", ar: "التغطية" },
  plan: { en: "Plan", ar: "الخطة" },
  band: { en: "Benefit band", ar: "فئة المنفعة" },
  copay: { en: "Copay", ar: "المساهمة" },
  validUntil: { en: "Valid until", ar: "صالح حتى" },
  capRemaining: { en: "Annual cap remaining", ar: "المتبقي من الحد السنوي" },
  visit: { en: "Visit gating", ar: "أهلية الزيارة" },
  visitOk: { en: "Visit allowed today", ar: "الزيارة مسموحة اليوم" },
  visitNo: { en: "Visit not allowed", ar: "الزيارة غير مسموحة" },
  book: { en: "Book appointment", ar: "احجز موعداً" },
  bookHint: {
    en: "Opens booking with this patient already chosen.",
    ar: "يفتح الحجز والمريض محدَّد بالفعل.",
  },
  card: { en: "Card", ar: "البطاقة" },
  memberNo: { en: "Member no.", ar: "رقم العضوية" },
  policyNo: { en: "Policy", ar: "الوثيقة" },
  limitsHeading: { en: "Remaining by benefit", ar: "المتبقي حسب المنفعة" },
  limitCategory: { en: "Benefit", ar: "المنفعة" },
  limitType: { en: "Limit", ar: "نوع الحد" },
  limitRemaining: { en: "Remaining", ar: "المتبقي" },
  limitsNone: {
    en: "This coverage carries no per-benefit limits.",
    ar: "لا توجد حدود لكل منفعة على هذه التغطية.",
  },
  dob: { en: "Date of birth", ar: "تاريخ الميلاد" },

  // ---- 32.6 — the benefit category, and what the answer is about ----
  category: { en: "Benefit category (optional)", ar: "فئة المنفعة (اختياري)" },
  categoryHelp: {
    en: "Naming it also returns the copay for that benefit.",
    ar: "تحديدها يعرض أيضاً المساهمة الخاصة بتلك المنفعة.",
  },
  categoryAny: { en: "Not decided yet", ar: "لم تُحدَّد بعد" },
  catConsult: { en: "Consultation", ar: "كشف" },
  catLab: { en: "Laboratory", ar: "مختبر" },
  catImaging: { en: "Imaging", ar: "أشعة" },
  catPharmacy: { en: "Pharmacy", ar: "صيدلية" },
  catReferral: { en: "Referral", ar: "إحالة" },

  scopeMembership: {
    en: "Membership only — no benefit category was named, so nothing here says whether a particular service "
      + "is covered.",
    ar: "العضوية فقط — لم تُحدَّد فئة منفعة، لذا لا شيء هنا يقول ما إذا كانت خدمة بعينها مغطاة.",
  },
  scopeBenefit: { en: "Coverage for", ar: "التغطية لـ" },
  copayTier: { en: "Tier", ar: "الشريحة" },
  copayFixed: { en: "Copay (fixed)", ar: "المساهمة (مبلغ ثابت)" },
  coinsurance: { en: "Coinsurance", ar: "نسبة المشاركة" },
  copayNone: {
    en: "This benefit carries no copay at the resolved tier.",
    ar: "لا توجد مساهمة على هذه المنفعة عند الشريحة المحددة.",
  },
} satisfies Record<string, Localized>;

/**
 * 32.6 — the walk-in categories, as eligibility migration 0006 CHECK-constrains them.
 *
 * <p>Five values and no free text, because the category is a coverage vocabulary the server validates: a
 * typed-in "Xray" would come back as "no active coverage for Xray" and read at the desk as a member without
 * cover. The empty choice is a real option, not a prompt — "not decided yet" is the honest state of a walk-in
 * who has not been triaged, and it is what the membership-only check is for.</p>
 */
const CATEGORIES = [
  { value: "CONSULT", label: "catConsult" },
  { value: "LAB", label: "catLab" },
  { value: "IMAGING", label: "catImaging" },
  { value: "PHARMACY", label: "catPharmacy" },
  { value: "REFERRAL", label: "catReferral" },
] as const;

export function ReceptionEligibility() {
  const api = useApi();
  const t = useLoc();
  const [identifier, setIdentifier] = useState("");
  const [name, setName] = useState("");
  const [category, setCategory] = useState<string | null>(null);
  const [status, setStatus] = useState<"idle" | "loading" | "error" | "success">("idle");
  const [result, setResult] = useState<EligibilityResult | null>(null);
  const [refusal, setRefusal] = useState<Refusal | null>(null);

  const ready = identifier.trim().length > 0 && name.trim().length > 0;

  /**
   * 33.9 — verify, THEN check.
   *
   * <p>This used to be `searchEligibility(query)` followed by `checkEligibility(hits[0].id)`. Two things were
   * wrong with that and only one of them was visible: a partial name was enough to open somebody's coverage,
   * and WHICH somebody depended on the order the database returned rows in. The screen showed the resulting
   * card with no indication that a choice had been made at all.</p>
   *
   * <p>The identifier now has to resolve to exactly one member and the name has to agree with it, and the
   * SERVICE decides both — a rule the browser applies is a rule for whoever is looking at this browser. The
   * refusal it sends back carries no identity, so this screen has no name on file to leak even if it wanted
   * to render one.</p>
   */
  async function run() {
    if (!ready) return;
    setStatus("loading");
    setRefusal(null);
    try {
      const v = await api.verifyBeneficiary(identifier.trim(), name.trim());
      if (!v.verified) {
        setResult(null);
        setRefusal(v.reason);
        setStatus("success");
        return;
      }
      // The category rides along when the desk knows it. Both the verdict and the copay are decided by
      // eligibility-service either way — this screen used to decide the verdict itself, from a cached
      // member status, and never called the service at all.
      const res = await api.checkEligibility(v.hit.id, category ?? undefined);
      setResult(res);
      setStatus("success");
    } catch (err) {
      void (err instanceof ApiError);
      setStatus("error");
    }
  }

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    void run();
  }

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={onSubmit} className="stack" aria-label={t(S.title)}>
          {/* The two halves of one question — what they presented, and who they say they are — so they read
              as a pair rather than as two unrelated steps. Stacks below 640px: side-by-side fields on a
              phone are two half-width boxes neither of which fits a national ID. */}
          <div className="elig-pair">
            <InputField
              label={t(S.fieldId)}
              value={identifier}
              onChange={(e) => setIdentifier(e.currentTarget.value)}
              autoComplete="off"
              inputMode="text"
              required
            />
            <InputField
              label={t(S.fieldName)}
              value={name}
              onChange={(e) => setName(e.currentTarget.value)}
              autoComplete="off"
              required
            />
          </div>
          <ComboboxField
            label={t(S.category)}
            help={t(S.categoryHelp)}
            options={CATEGORIES.map((c) => ({ value: c.value, label: t(S[c.label]) }))}
            value={category}
            onChange={(v) => setCategory(v || null)}
            placeholder={t(S.categoryAny)}
          />
          <div>
            {/* Disabled until BOTH are given: the check cannot run on one of them, and a button that
                accepts the click and then does nothing reads as a broken screen rather than as a rule. */}
            <Button type="submit" variant="primary" disabled={!ready}
              leadingIcon={<Icon name="check2" />} loading={status === "loading"}>
              {t(S.check)}
            </Button>
          </div>
        </form>
      </Card>

      {/* Async outcome — announced politely for screen readers. */}
      <div aria-live="polite" className="stack" style={{ marginTop: "var(--sp4)" }}>
        {status === "idle" && (
          <Card style={{ padding: "var(--sp5)" }}>
            <p className="muted">{t(S.idle)}</p>
          </Card>
        )}
        {status === "loading" && (
          <Card style={{ padding: "var(--sp5)" }}>
            <div className="async-loading" role="status">
              <span className="mrs-spin" aria-hidden="true" />
              <span>{t(S.loading)}</span>
            </div>
          </Card>
        )}
        {status === "error" && (
          <Card style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            {/*
             * A failed lookup is an ALERT, not a badge. `StatusChip` is the vocabulary for the state of a
             * THING on screen — a membership is Active, a visit is allowed — and it was being borrowed here
             * to report the outcome of something the operator just did. Every other screen in the portal
             * says that with `InlineAlert`, which carries `role="alert"` and the cross icon, so a reader
             * who has learned what a failure looks like once recognises it everywhere.
             */}
            <InlineAlert tone="bad" data-testid="elig-error">{t(S.error)}</InlineAlert>
            <div>
              <Button variant="secondary" onClick={() => void run()}>
                {t(S.retry)}
              </Button>
            </div>
          </Card>
        )}
        {status === "success" && !result && refusal && (
          <RefusalCard reason={refusal} t={t} />
        )}
        {status === "success" && result && (
          <ResultCard result={result} t={t} S={S} identifier={identifier.trim()} />
        )}
      </div>
    </>
  );
}

/** The three ways a verified lookup can say no — the service's codes, kept as a type so a new one cannot be
 *  added on the server and quietly render as nothing here. */
type Refusal = "not-found" | "name-mismatch" | "name-too-short";

/**
 * 33.9 — a refusal is a state with an instruction, not an empty screen.
 *
 * <p>The three lead to different actions and are told apart for that reason: re-read the digits, ask them to
 * repeat their name, or type more. A mismatch is the one that carries a warning tone, because it is the one
 * where continuing would put another member's coverage in front of the desk — and it says so, since an
 * operator who reads "no match" is likely to try again more loosely rather than stop.</p>
 *
 * <p>None of them names the person on file. The service does not send it: see the api client.</p>
 */
function RefusalCard({ reason, t }: { reason: Refusal; t: (l: Localized) => string }) {
  const copy = {
    "not-found": { title: S.noneTitle, body: S.noneBody, tone: "info" as const, testid: "elig-empty" },
    "name-mismatch": { title: S.mismatchTitle, body: S.mismatchBody, tone: "warn" as const, testid: "elig-mismatch" },
    "name-too-short": { title: S.shortTitle, body: S.shortBody, tone: "info" as const, testid: "elig-short" },
  }[reason];
  return (
    <Card style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }} data-testid={copy.testid}>
      <h2 className="empty-title">{t(copy.title)}</h2>
      {copy.tone === "warn"
        ? <InlineAlert tone="warn">{t(copy.body)}</InlineAlert>
        : <p className="muted" style={{ margin: 0 }}>{t(copy.body)}</p>}
    </Card>
  );
}

/**
 * 33.9c — the way on from a verdict.
 *
 * <p>An eligible member at the desk is there to be seen, and the next thing the operator does is book. That
 * meant leaving this screen, opening Book Appointment, and typing the same identifier a second time into a
 * search — after a lookup that had just resolved the person exactly. Re-identifying somebody the platform has
 * already identified is not only work, it is a second chance to land on the wrong record.</p>
 *
 * <p><b>Gated on the VISIT GATE, not on the verdict.</b> `visitGate.allowed` is the membership answer — may
 * this person be seen today — and it is the question booking asks. A benefit that needs authorisation is a
 * soft no on the CARE and not a closed door at reception, so offering the booking on the benefit verdict
 * would hide the button from someone who may perfectly well be booked in.</p>
 *
 * <p><b>And on the permission.</b> `/…/book` is portal-relative and not every portal that checks eligibility
 * carries it — a registration officer holds `eligibility.check` without `appointments.book`. Offering a
 * control that leads to a route the caller cannot reach is worse than not offering it.</p>
 */
function BookFromResult({ result, identifier, t }: {
  result: EligibilityResult;
  identifier: string;
  t: (l: Localized) => string;
}) {
  const { session } = useAuth();
  const { pathname } = useLocation();
  const role = session?.role as Role | undefined;

  if (!result.visitGate.allowed) return null;
  if (!role || !hasPermission(permissionsForRole(role), "appointments.book")) return null;

  // Sibling of this screen, so the link is right under /reception, /branch and /beneficiaries alike rather
  // than hardcoding one portal's path — this screen is mounted under more than one.
  const to = `${pathname.replace(/\/[^/]*$/, "")}/book`;
  // The identifier the SERVICE resolved, not the string that was typed: it is the canonical one, and it is
  // what booking's own lookup will match.
  const q = result.beneficiary.cardNumber || identifier;

  return (
    <div className="stack" style={{ gap: "var(--sp1)" }}>
      <Link className="mrs-btn mrs-primary" to={`${to}?q=${encodeURIComponent(q)}`}>
        <Icon name="calendar" width={17} height={17} aria-hidden="true" />
        {t(S.book)}
      </Link>
      <p className="muted" style={{ margin: 0, fontSize: "var(--fs-caption)" }}>{t(S.bookHint)}</p>
    </div>
  );
}

function ResultCard({ result, t, S, identifier }: {
  result: EligibilityResult;
  t: (l: Localized) => string;
  S: Record<string, Localized>;
  identifier: string;
}) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const b = result.beneficiary;
  const c = result.coverage;
  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div className="result-head">
        <div>
          <h2 style={{ margin: 0 }}>{t(b.name)}</h2>
          {/* Card and member number are DIFFERENT identifiers (33.9b), and the desk is holding one of them.
              The member number is shown beside the card only when it differs — printing the same string
              twice under two labels teaches a reader that the labels do not mean anything. */}
          <p className="muted" style={{ margin: "4px 0 0" }}>
            {t(S.card)}: <span className="tnum">{b.cardNumber}</span>
            {b.memberNo && b.memberNo !== b.cardNumber && (
              <> · {t(S.memberNo)}: <span className="tnum">{b.memberNo}</span></>
            )}
            {b.dateOfBirth && <> · {t(S.dob)}: <span className="tnum">{b.dateOfBirth}</span></>}
          </p>
        </div>
        <StatusChip kind={result.status.kind} label={t(result.status.label)} />
      </div>

      {/* 32.6 — WHAT this verdict is about, said before the numbers under it.
          "Eligible" at membership scope and "Eligible" for LAB are the same word and different facts, and
          the desk reads whichever one it is told. */}
      {result.scope === "membership" ? (
        <InlineAlert tone="info">{t(S.scopeMembership)}</InlineAlert>
      ) : (
        <p className="muted" style={{ margin: 0 }}>
          {t(S.scopeBenefit)} <strong>{result.benefitCategory}</strong>
        </p>
      )}

      {/*
        The coverage details are shown in FULL, at either scope.

        <p>They used to be three rows — a plan name, the category list, and one monetary limit — and the
        plan name was a hardcoded literal, so the card printed a plan that was not a plan while the per-
        benefit limits the projection actually carries were dropped on the way in. A member with four
        consultations and EGP 3,200 of laboratory cover left was summarised as one number, and the desk
        could not answer "how many consultations do they have?" from the screen that question belongs on.</p>

        <p>Not gated on scope. Naming a benefit category asks an ADDITIONAL question — is this service
        covered, and what does it cost — it does not make the rest of the coverage less true, and a desk
        that has to run the check twice to see the whole picture will run it once.</p>
      */}
      {c && (
        <>
          <dl className="kv-grid" aria-label={t(S.coverage)}>
            {/* Rendered only when a plan is genuinely known — see zCoverage.planName. */}
            {c.planName && <div><dt>{t(S.plan)}</dt><dd>{t(c.planName)}</dd></div>}
            {c.policyNo && <div><dt>{t(S.policyNo)}</dt><dd className="tnum">{c.policyNo}</dd></div>}
            <div><dt>{t(S.band)}</dt><dd>{t(c.band)}</dd></div>
            {c.validUntil && <div><dt>{t(S.validUntil)}</dt><dd className="tnum">{c.validUntil}</dd></div>}
            {c.annualCapRemaining != null && (
              <div><dt>{t(S.capRemaining)}</dt><dd className="tnum">{fmt.money(c.annualCapRemaining)}</dd></div>
            )}
          </dl>
          <CoverageLimits limits={c.limits} t={t} fmt={fmt} />
        </>
      )}

      <CostShareBlock share={result.costShare} t={t} S={S} fmt={fmt} />

      <div className="result-head" style={{ alignItems: "center" }}>
        {result.visitGate.allowed ? (
          <StatusChip kind="ok" label={t(S.visitOk)} />
        ) : (
          <StatusChip kind="warn" label={result.visitGate.reason ? t(result.visitGate.reason) : t(S.visitNo)} />
        )}
        <BookFromResult result={result} identifier={identifier} t={t} />
      </div>
    </Card>
  );
}

/**
 * 33.9b — every limit on the coverage, not the one the client happened to pick.
 *
 * <p>The reception card has always carried a `{category, limitType, remaining}` row per limit per active
 * coverage. The api client took the first monetary one for the headline and discarded the rest, so the
 * screen could show a member's annual cap and nothing about the four consultations or the one imaging study
 * they had left.</p>
 *
 * <p><b>The limit TYPE is rendered, not inferred.</b> `Count` and `Amount` are different quantities and the
 * only thing that distinguishes "4" from "EGP 4" is which one this says. Money is formatted as money;
 * everything else is shown as the plain number it is, because guessing a unit for a vocabulary the service
 * owns is how a count of visits becomes a currency figure on a desk.</p>
 */
function CoverageLimits({
  limits, t, fmt,
}: {
  limits: EligibilityResult["coverage"] extends infer C ? C extends { limits: infer L } ? L : never : never;
  t: (l: Localized) => string;
  fmt: ReturnType<typeof useFormat>;
}) {
  if (limits.length === 0) return <p className="muted" style={{ margin: 0 }}>{t(S.limitsNone)}</p>;
  return (
    <table className="mini-table" aria-label={t(S.limitsHeading)}>
      <caption className="section-h" style={{ textAlign: "start", marginBottom: "var(--sp2)" }}>
        {t(S.limitsHeading)}
      </caption>
      <thead>
        <tr>
          <th scope="col">{t(S.limitCategory)}</th>
          <th scope="col">{t(S.limitType)}</th>
          <th scope="col">{t(S.limitRemaining)}</th>
        </tr>
      </thead>
      <tbody>
        {limits.map((l, i) => (
          <tr key={`${l.category}-${l.limitType}-${i}`}>
            <td>{l.category}</td>
            <td>{l.limitType}</td>
            <td className="tnum">
              {/^amount|money|cap$/i.test(l.limitType) ? fmt.money(l.remaining) : l.remaining}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * 32.6 — what the member pays, or the sentence that says why nobody can tell them.
 *
 * <p>There is no branch here that renders a blank or a zero. The idle text on this screen has promised a
 * copay since phase 2 and the code could not produce one: `checkEligibility` never called the service, so
 * `copayPercent` was `undefined` on every result and the row was skipped every time. A promise on a screen
 * with no code path behind it is worse than a missing feature, because nobody looking at the screen can tell.</p>
 */
function CostShareBlock({
  share, t, S, fmt,
}: {
  share: EligibilityResult["costShare"];
  t: (l: Localized) => string;
  S: Record<string, Localized>;
  fmt: ReturnType<typeof useFormat>;
}) {
  if (!share.known) return <InlineAlert tone="info">{t(share.why)}</InlineAlert>;

  const nothingToPay =
    share.copayPercent == null && share.copayFixed == null && share.coinsurancePercent == null;

  return (
    <dl className="kv-grid" aria-label={t(S.copay)}>
      {share.tierCode && <div><dt>{t(S.copayTier)}</dt><dd className="tnum">{share.tierCode}</dd></div>}
      {share.copayPercent != null && (
        <div><dt>{t(S.copay)}</dt><dd className="tnum">{share.copayPercent}%</dd></div>
      )}
      {share.copayFixed != null && (
        <div><dt>{t(S.copayFixed)}</dt><dd className="tnum">{fmt.money(share.copayFixed)}</dd></div>
      )}
      {share.coinsurancePercent != null && (
        <div><dt>{t(S.coinsurance)}</dt><dd className="tnum">{share.coinsurancePercent}%</dd></div>
      )}
      {/* A quote that resolved and came back with no charge is a REAL answer, and it is said in words. An
          empty list here would look identical to the case above, which is the opposite answer. */}
      {nothingToPay && <div><dt>{t(S.copay)}</dt><dd>{t(S.copayNone)}</dd></div>}
    </dl>
  );
}
