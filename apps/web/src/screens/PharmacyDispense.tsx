import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, DataTable, Icon, InlineAlert, InputField, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, Prescription } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { PatientContextBar } from "./PatientProfile";
import { PageHeader, useLoc } from "./_shared";
import { ApiError } from "../api/http";
import { useFormat } from "../i18n/useFormat";
import { RequestExtensionModal } from "./extensions/RequestExtensionModal";

const S = {
  title: { en: "Dispense", ar: "الصرف" },
  empty: { en: "No prescriptions awaiting dispense.", ar: "لا توجد وصفات بانتظار الصرف." },
  patient: { en: "Patient", ar: "المريض" },
  rxNo: { en: "Prescription", ar: "الوصفة" },
  submitted: { en: "Written", ar: "تاريخ الوصف" },

  // ---- search ----
  searchTitle: { en: "Find a member's prescriptions", ar: "ابحث عن وصفات العضو" },
  searchHint: {
    en: "Search by prescription number, or by TWO of the member's identifiers.",
    ar: "ابحث برقم الوصفة، أو باثنين من معرّفات العضو.",
  },
  fRxNo: { en: "Prescription number", ar: "رقم الوصفة" },
  fCard: { en: "Card number", ar: "رقم البطاقة" },
  fMember: { en: "Member number", ar: "رقم العضوية" },
  fPassport: { en: "Passport", ar: "جواز السفر" },
  phRxNo: { en: "RX-2026-000202", ar: "RX-2026-000202" },
  search: { en: "Search", ar: "بحث" },
  clear: { en: "Clear", ar: "مسح" },
  startHere: {
    en: "Enter a prescription number, or two of the member's identifiers, to begin.",
    ar: "أدخل رقم الوصفة أو اثنين من معرّفات العضو للبدء.",
  },
  twoIdentifiers: {
    en: "A card number on its own is not enough — it is printed on something that gets shared and "
      + "photographed. Add the member number or passport, or search by prescription number instead.",
    ar: "رقم البطاقة وحده لا يكفي — فهو مطبوع على ما يُتداول ويُصوَّر. أضف رقم العضوية أو جواز السفر، "
      + "أو ابحث برقم الوصفة.",
  },
  directoryDown: {
    en: "The patient directory could not be reached, so these identifiers could not be checked. "
      + "This is NOT a report that the member has no prescriptions — try again.",
    ar: "تعذّر الوصول إلى دليل المرضى، لذلك لم يتم التحقق من هذه المعرّفات. هذا ليس تقريراً بعدم وجود "
      + "وصفات — أعد المحاولة.",
  },
  noMatch: {
    en: "No dispensable prescription matches that search.",
    ar: "لا توجد وصفة قابلة للصرف تطابق هذا البحث.",
  },
  prescriber: { en: "Prescriber", ar: "الواصف" },
  lines: { en: "Lines", ar: "البنود" },
  state: { en: "State", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  open: { en: "Open", ar: "فتح" },
  fail: { en: "Dispense failed.", ar: "فشل الصرف." },

  // ---- expired + validity extension ----
  expired: { en: "Expired", ar: "منتهية" },
  expiredOn: { en: "Expired {date}", ar: "انتهت في {date}" },
  expiredTitle: { en: "This prescription has expired", ar: "انتهت صلاحية هذه الوصفة" },
  expiredBody: {
    en: "It cannot be dispensed. A prescription is a decision made about a patient on a particular day, and "
      + "this one is past the window it was written for. The approval team can revalidate it — the patient "
      + "does not need to go back to a doctor for a new one.",
    ar: "لا يمكن صرفها. الوصفة قرار اتُّخذ بشأن مريض في يوم معيّن، وقد تجاوزت هذه المدة المحددة لها. "
      + "يمكن لفريق الموافقات إعادة تفعيلها — ولا يحتاج المريض إلى العودة للطبيب للحصول على وصفة جديدة.",
  },
  requestExtension: { en: "Request extension", ar: "طلب تمديد" },
  requestTitle: { en: "Ask for this prescription to be revalidated", ar: "طلب إعادة تفعيل هذه الوصفة" },
  reason: { en: "Why does this need extending?", ar: "لماذا يحتاج هذا إلى تمديد؟" },
  reasonHint: {
    en: "The approval team sees this and nothing else. Say what happened — the whole decision rests on it.",
    ar: "لن يرى فريق الموافقات سوى هذا. اذكر ما حدث — فالقرار كله يستند إليه.",
  },
  reasonPlaceholder: {
    en: "e.g. patient is mid-course and could not travel before it lapsed",
    ar: "مثال: المريض في منتصف الجرعات ولم يستطع الحضور قبل انتهاء الصلاحية",
  },
  reasonTooShort: {
    en: "Write at least a short sentence. An approver with an empty box is deciding on who asked, not on why.",
    ar: "اكتب جملة قصيرة على الأقل. المُوافِق بدون سبب يقرّر بناءً على من طلب، لا على السبب.",
  },
  send: { en: "Send request", ar: "إرسال الطلب" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  requestSent: {
    en: "Sent to the approval team as {authNo}. The prescription stays expired until they decide.",
    ar: "أُرسل إلى فريق الموافقات برقم {authNo}. تبقى الوصفة منتهية حتى يصدر قرارهم.",
  },
  alreadyRequested: {
    en: "Someone has already asked for this one. It is with the approval team.",
    ar: "سبق أن طلب أحدهم ذلك. الطلب لدى فريق الموافقات.",
  },
  requestFailed: { en: "The request could not be sent.", ar: "تعذّر إرسال الطلب." },
  review: { en: "Review", ar: "عرض" },
} satisfies Record<string, Localized>;

/**
 * The dispensing counter.
 *
 * <b>Search-first, not browse-first.</b> This screen used to list every dispensable prescription in the
 * tenant — a board a pharmacist scrolls looking for the person standing in front of them. That is both the
 * wrong workflow and the wrong disclosure: it puts other patients' prescriptions on screen to reach one.
 * The counter's real question is "what do I have for THIS member", so the screen opens on the question.
 *
 * Two ways in, and the asymmetry is deliberate. A PRESCRIPTION NUMBER identifies the prescription on its
 * own — it is the reference printed on what the patient is holding. A CARD NUMBER does not identify a
 * person: it is printed on something that gets shared, photographed and reused, so it takes a second
 * identifier alongside it (doc 43 §7 D5). The server enforces that; this screen explains it.
 */
export function PharmacyDispense() {
  const api = useApi();
  const t = useLoc();
  const [form, setForm] = useState({ rxNo: "", cardNumber: "", memberNo: "", passport: "" });
  const [results, setResults] = useState<Prescription[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);
  const navigate = useNavigate();

  const field = (k: keyof typeof form) => ({
    value: form[k],
    onChange: (e: { currentTarget: { value: string } }) => {
      const v = e.currentTarget.value;
      setForm((prev) => ({ ...prev, [k]: v }));
    },
  });

  async function search() {
    setBusy(true);
    setError(null);
    setSelected(null);
    try {
      const rows = await api.pharmacySearch(form);
      setResults(rows);
    } catch (e) {
      setResults(null);
      // The three refusals mean three different things, and only one of them is about the patient. A 503
      // rendered as "no prescriptions" would be a wrong clinical answer with a calm face on it.
      const status = e instanceof ApiError ? e.status : 0;
      setError(status === 422 ? S.twoIdentifiers : status === 503 ? S.directoryDown : S.fail);
    } finally {
      setBusy(false);
    }
  }

  function clear() {
    setForm({ rxNo: "", cardNumber: "", memberNo: "", passport: "" });
    setResults(null);
    setSelected(null);
    setError(null);
  }

  const cols: Column<Prescription>[] = [
    { key: "rxNo", header: t(S.rxNo), cell: (r) => <span className="tnum">{r.rxNo}</span> },
    { key: "prescriber", header: t(S.prescriber), cell: (r) => t(r.prescriber.label) },
    { key: "lines", header: t(S.lines), cell: (r) => r.lines.length, numeric: true },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "open",
      header: t(S.action),
      // Same button either way: it selects the row, and the panel decides what can be DONE with it. An
      // "Open" that leads to a panel saying "you cannot open this" would be a promise the screen breaks —
      // and the server refuses to open an expired prescription anyway (409), so offering it would put the
      // pharmacist through a failure the row already knew about.
      // Opening navigates to the prescription's OWN page rather than filling a panel beside this table.
      // Dispensing is the task and the search is only how you reach it; giving it a URL is also what lets a
      // pharmacist reload, or hand the screen to a colleague, without losing their place.
      //
      // Expired prescriptions stay on the panel here: that path is a recovery (ask for revalidation), not a
      // dispense, and it does not need the page.
      cell: (r) => (
        <Button
          size="sm"
          variant={selected === r.id ? "primary" : "secondary"}
          onClick={() => (r.expired ? setSelected(r.id) : navigate(`/pharmacy/rx/${encodeURIComponent(r.rxNo)}`))}
        >
          {r.expired ? t(S.review) : t(S.open)}
        </Button>
      ),
    },
  ];

  const active = results?.find((p) => p.id === selected) ?? null;
  const canSearch = Object.values(form).some((v) => v.trim() !== "") && !busy;

  return (
    <>
      <PageHeader title={t(S.title)} />

      <Card as="section" style={{ padding: "var(--sp5)", marginBottom: "var(--sp4)" }}>
        <h2 className="section-h" style={{ marginBlockStart: 0 }}>{t(S.searchTitle)}</h2>
        <p className="muted" style={{ marginBlockStart: 0 }}>{t(S.searchHint)}</p>
        {/* A real form, so Enter submits — a counter is typed at, not clicked through. */}
        <form
          className="rx-search"
          onSubmit={(e) => { e.preventDefault(); if (canSearch) void search(); }}
        >
          <InputField label={t(S.fRxNo)} placeholder={t(S.phRxNo)} {...field("rxNo")} />
          <InputField label={t(S.fCard)} {...field("cardNumber")} />
          <InputField label={t(S.fMember)} {...field("memberNo")} />
          <InputField label={t(S.fPassport)} {...field("passport")} />
          <div className="rx-search-actions">
            <Button leadingIcon={<Icon name="search" />} type="submit" variant="primary" loading={busy} disabled={!canSearch}>{t(S.search)}</Button>
            <Button type="button" variant="ghost" onClick={clear}>{t(S.clear)}</Button>
          </div>
        </form>
        {/* aria-live: the outcome of a search the user just triggered, announced without moving focus. */}
        <div aria-live="polite">
          {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
          {!error && results?.length === 0 && <InlineAlert tone="info">{t(S.noMatch)}</InlineAlert>}
        </div>
      </Card>

      {/* ONE column, full width. This was a two-column split with a "select a prescription to dispense"
          placeholder holding the right half open — and once Open started navigating to the prescription's own
          page, nothing ever filled it for a dispensable prescription. A permanent placeholder is worse than
          no panel: it reads as a pane that has failed to load, and it was costing the results table half the
          viewport to say nothing. */}
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        {results === null || results.length === 0 ? (
          <p className="muted" style={{ margin: "var(--sp3)" }}>{t(S.startHere)}</p>
        ) : (
          <DataTable columns={cols} rows={results} rowKey={(r) => r.id} caption={t(S.title)} />
        )}
      </Card>

      {/* The one thing that still opens in place: an EXPIRED prescription, where the action is a recovery
          rather than a dispense. It appears under the row it belongs to, and only when one is selected. */}
      {active?.expired && (
        <div style={{ marginBlockStart: "var(--sp4)" }}>
          {/* Phase 20 — the context bar. It NAMES the person the medication is for, which is the check a
              pharmacist actually performs before handing anything over; allergies ride along because the
              drug-allergy conflict is the other reason this strip is on a dispensing screen (design 39 §4). */}
          <PatientContextBar beneficiaryId={active.patient.id} />
          <ExpiredPanel key={active.id} rx={active} t={t} />
        </div>
      )}
    </>
  );
}

/**
 * What the counter sees instead of a dispense form when the prescription has lapsed.
 *
 * <b>Why a panel and not a disabled form.</b> A greyed-out dispense form says "you cannot do this" and
 * stops. The pharmacist still has a patient in front of them who needs the medication, and the recovery —
 * asking the approval team to revalidate it — is two minutes of work that nobody would guess is available.
 * Sending them away to get a fresh prescription from a doctor is a wasted journey, and for a refugee
 * beneficiary often a second appointment and a second bus fare.
 *
 * <b>It never pretends the medication can be handed over.</b> Requesting an extension changes nothing about
 * today: the prescription stays expired until a decision lands, and the confirmation says exactly that
 * rather than letting "request sent" read as "sorted".
 */
function ExpiredPanel({ rx, t }: { rx: Prescription; t: (l: Localized) => string }) {
  const { date } = useFormat();
  const [asking, setAsking] = useState(false);
  const [sent, setSent] = useState<Localized | null>(null);

  return (
    <Card as="section" style={{ padding: "var(--sp5)" }}>
      {/* `.result-head` is what DispensePanel titles itself with. The two panels alternate in the same slot
          and must not drift apart visually — a second class here would be a second look for the same thing. */}
      <div className="result-head">
        <h2 style={{ margin: 0 }} className="tnum">{rx.rxNo}</h2>
        <StatusChip kind="bad" label={t(S.expired)} />
      </div>

      <InlineAlert tone="bad">
        <strong>{t(S.expiredTitle)}</strong>
        {rx.expiresAt ? ` — ${t(S.expiredOn).replace("{date}", date(rx.expiresAt))}` : ""}
        <p style={{ margin: "var(--sp2) 0 0" }}>{t(S.expiredBody)}</p>
      </InlineAlert>

      {/* The medication is still listed. A pharmacist deciding whether this is worth chasing needs to know
          WHAT lapsed — "an expired prescription" and "the patient's metformin" are different questions. */}
      <ul className="rxv-lines" style={{ marginBlockStart: "var(--sp3)" }}>
        {rx.lines.map((l) => (
          <li key={l.id} className="rxv-line">
            <div className="rxv-line-h">
              <span className="rxv-drug">{t(l.drug.label)}</span>
              <span className="muted">{l.dose}</span>
            </div>
          </li>
        ))}
      </ul>

      <div aria-live="polite">
        {sent && <InlineAlert tone="info">{t(sent)}</InlineAlert>}
      </div>

      {!sent && (
        <div className="rx-actions">
          <Button variant="primary" onClick={() => setAsking(true)}>{t(S.requestExtension)}</Button>
        </div>
      )}

      <RequestExtensionModal
        open={asking}
        onOpenChange={setAsking}
        item={{
          itemType: "Prescription",
          itemId: rx.id,
          itemReference: rx.rxNo,
          beneficiaryId: rx.patient.id,
          expiredAt: rx.expiresAt ?? null,
        }}
        placeholder={S.reasonPlaceholder}
        sentMessage={S.requestSent}
        alreadyRequestedMessage={S.alreadyRequested}
        onSent={setSent}
      />
    </Card>
  );
}
