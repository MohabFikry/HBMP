import { useState } from "react";
import {
  Button,
  Card,
  DataTableView,
  InlineAlert,
  InputField,
  SegmentedControl,
  StatusChip,
  TextareaField,
  useTableQuery,
  useToast,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AdjudicationRow, ClaimDecisionKind, Localized } from "@mersal/contracts";
import { CLAIM_DECISION_KINDS, CLAIM_REASON_CODES, zClaimDecisionRequest } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { ApiError } from "../api/http";
import { AsyncSection, CodeList, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Adjudication", ar: "البتّ في البنود" },
  intro: {
    en: "One row per claim LINE, not per claim — this is the queue the adjudication engine fills, and each line "
      + "is decided on its own. Codes and amounts only: whether a service was rendered is a yes or no derived "
      + "from the fulfilment record, never what it found.",
    ar: "صف واحد لكل بند من بنود المطالبة، لا لكل مطالبة — فهذه هي القائمة التي يملؤها محرّك البتّ، ويُبتّ في كل "
      + "بند على حدة. الأكواد والمبالغ فقط: وتنفيذ الخدمة من عدمه نعم أو لا مستنتجة من سجل التنفيذ، لا محتواه.",
  },
  empty: { en: "No lines are awaiting a decision.", ar: "لا توجد بنود تنتظر قراراً." },
  noMatches: { en: "No lines match. Change the search or clear the filters.", ar: "لا توجد بنود مطابقة. عدّل البحث أو أزل عوامل التصفية." },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Claim number, service code or reason code", ar: "رقم المطالبة أو رمز الخدمة أو رمز السبب" },

  claimNo: { en: "Claim", ar: "المطالبة" },
  code: { en: "Code", ar: "الرمز" },
  serviceDate: { en: "Service date", ar: "تاريخ الخدمة" },
  qty: { en: "Qty", ar: "الكمية" },
  billed: { en: "Billed", ar: "المفوتر" },
  contract: { en: "Contract", ar: "التعاقدي" },
  recommendation: { en: "Engine says", ar: "توصية النظام" },
  reasons: { en: "Reason codes", ar: "رموز الأسباب" },
  rendered: { en: "Rendered", ar: "مُنفَّذ" },
  yes: { en: "Yes", ar: "نعم" },
  no: { en: "Not recorded", ar: "غير مسجَّل" },
  action: { en: "Action", ar: "إجراء" },
  decide: { en: "Decide", ar: "قرار" },

  fRec: { en: "Engine recommendation", ar: "توصية النظام" },
  fAny: { en: "Any", ar: "الكل" },
  fApprove: { en: "Approve", ar: "اعتماد" },
  fDeny: { en: "Deny", ar: "رفض" },
  fManual: { en: "Manual review", ar: "مراجعة يدوية" },
  fValue: { en: "Value at least", ar: "القيمة من" },

  pick: { en: "Select a line to decide it.", ar: "اختر بنداً للبتّ فيه." },
  decision: { en: "Decision", ar: "القرار" },
  dApprove: { en: "Approve", ar: "اعتماد" },
  dPartial: { en: "Partial", ar: "جزئي" },
  dDeny: { en: "Deny", ar: "رفض" },
  dAdjust: { en: "Adjust", ar: "تعديل" },
  dRequestInfo: { en: "Request info", ar: "طلب معلومات" },
  dRouteClinical: { en: "To clinical review", ar: "إلى المراجعة السريرية" },
  allowedAmount: { en: "Allowed amount", ar: "المبلغ المسموح" },
  allowedReq: { en: "An allowed amount is required for a partial approval or an adjustment.", ar: "المبلغ المسموح إلزامي للموافقة الجزئية أو التعديل." },
  rationale: { en: "Rationale", ar: "المبرر" },
  rationaleHint: { en: "Required for anything other than a plain approval.", ar: "إلزامي لأي قرار غير الاعتماد الكامل." },
  rationaleReq: { en: "A rationale is required for anything other than a plain approval.", ar: "المبرر إلزامي لأي قرار غير الاعتماد الكامل." },
  reasonCodes: { en: "Reason codes", ar: "رموز الأسباب" },
  reasonHint: {
    en: "Picked from the fifteen the adjudicator recognises. A code it does not know is refused after you have "
      + "written the rationale, which is work thrown away.",
    ar: "تُختار من الرموز الخمسة عشر التي يعرفها المحرّك. الرمز غير المعروف يُرفض بعد كتابة المبرر، وهو جهد يضيع.",
  },
  submit: { en: "Record decision", ar: "تسجيل القرار" },
  recorded: { en: "Decision recorded.", ar: "تم تسجيل القرار." },
  replayed: { en: "Already recorded (idempotent replay).", ar: "مُسجّل مسبقاً (إعادة متكافئة)." },

  pendingTitle: { en: "Waiting for a second approver", ar: "بانتظار معتمد ثانٍ" },
  pendingBody: {
    en: "This decision is above the dual-control threshold, so it is held until a second, distinct approver "
      + "confirms it. Nothing has been refused — the decision is recorded and pending.",
    ar: "هذا القرار يتجاوز حدّ الرقابة المزدوجة، فيُحتجَز حتى يؤكده معتمد ثانٍ مختلف. لم يُرفض شيء — القرار مسجَّل "
      + "ومعلَّق.",
  },

  // The three SoD refusals, each said as itself. A 403 reading only "forbidden" on a segregation-of-duties
  // check teaches the reviewer the system is broken rather than that the control is working.
  sodOriginator: {
    en: "You created this claim, so you cannot adjudicate it. Somebody who did not originate it must decide.",
    ar: "أنت من أنشأ هذه المطالبة، فلا يمكنك البتّ فيها. يلزم أن يقرر شخص لم ينشئها.",
  },
  sodProvider: {
    en: "You are affiliated with the claiming provider, so you cannot decide their claim.",
    ar: "أنت مرتبط بمقدم الخدمة صاحب المطالبة، فلا يمكنك البتّ في مطالبته.",
  },
  sodSameDecider: {
    en: "You made the first decision on this line. A second, distinct approver is required to confirm it.",
    ar: "أنت من اتخذ القرار الأول على هذا البند. يلزم معتمد ثانٍ مختلف لتأكيده.",
  },
  conflict: { en: "This line was decided by somebody else while you were looking at it. Reload the queue.", ar: "بتّ شخص آخر في هذا البند أثناء اطلاعك عليه. أعد تحميل القائمة." },
  failed: { en: "Could not record the decision.", ar: "تعذّر تسجيل القرار." },
} satisfies Record<string, Localized>;

const DECISION_LABEL: Record<ClaimDecisionKind, Localized> = {
  Approve: S.dApprove,
  PartiallyApprove: S.dPartial,
  Deny: S.dDeny,
  Adjust: S.dAdjust,
  RequestInfo: S.dRequestInfo,
  RouteToClinical: S.dRouteClinical,
};

const RECOMMENDATION_KIND: Record<string, "ok" | "bad" | "warn" | "neu"> = {
  Approve: "ok",
  Deny: "bad",
  RequiresManualReview: "warn",
};

/**
 * The claims officer's decision workspace.
 *
 * <b>Why it did not exist.</b> `claims_officer` holds `claims:decide`, `claims:adjudicate`, `claims:adjust`,
 * `claims:appeal`, `claims:batch`, `claims:submit` and `claims:reimburse:submit` — every write scope the role
 * needs. The portal was three read screens. Line decisions with reason codes, the dual-control hand-off and
 * the three segregation-of-duties refusals were all implemented server-side and none of them was reachable.
 *
 * A migration comment in identity/0005 records the same gap being found once before, one layer down: the
 * scopes were granted then, and the screens were never built.
 *
 * <b>Minimum-necessary is unchanged.</b> `resultExists` is a boolean the server derives from the fulfilment
 * linkage, so an officer can confirm a service was rendered without reading what it found. There is no field
 * on this screen, or on the DTO behind it, that can carry a diagnosis.
 */
export function ClaimsAdjudication() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const [recommendation, setRecommendation] = useState<string>("");
  const [minValue, setMinValue] = useState<string>("");
  const [selected, setSelected] = useState<string | null>(null);

  // Server-side, because these are the filters the endpoint accepts and this queue can run to hundreds of
  // lines. `minValue` is committed on blur rather than per keystroke — a refetch per digit is worse than a
  // beat's delay.
  const [committedMin, setCommittedMin] = useState<number | undefined>(undefined);
  const state = useAsync<AdjudicationRow[]>(
    () => api.adjudicationQueue({ recommendation: recommendation || undefined, minValue: committedMin }),
    [recommendation, committedMin],
  );
  const rows = state.data ?? [];
  const selectedRow = rows.find((r) => r.claimLineId === selected) ?? null;

  const cols: Column<AdjudicationRow>[] = [
    { key: "claimNo", header: t(S.claimNo), cell: (r) => <span className="tnum">{r.claimNo}</span>, sortable: true, sortValue: (r) => r.claimNo },
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    { key: "serviceDate", header: t(S.serviceDate), cell: (r) => <span className="tnum">{fmt.date(r.serviceDate)}</span>, sortable: true, sortValue: (r) => r.serviceDate },
    { key: "qty", header: t(S.qty), cell: (r) => fmt.number(r.quantity), numeric: true, sortable: true, sortValue: (r) => r.quantity },
    { key: "billed", header: t(S.billed), cell: (r) => fmt.money(r.billedAmount), numeric: true, sortable: true, sortValue: (r) => r.billedAmount },
    { key: "contract", header: t(S.contract), cell: (r) => (r.contractPrice == null ? <span className="muted">—</span> : fmt.money(r.contractPrice)), numeric: true, sortable: true, sortValue: (r) => r.contractPrice ?? -1 },
    {
      key: "rec",
      header: t(S.recommendation),
      cell: (r) =>
        r.systemRecommendation
          ? <StatusChip kind={RECOMMENDATION_KIND[r.systemRecommendation] ?? "neu"} label={r.systemRecommendation} />
          : <span className="muted">—</span>,
      sortable: true,
      sortValue: (r) => r.systemRecommendation ?? "",
    },
    {
      key: "reasons",
      // The codes verbatim. This is the vocabulary the server validates against and the one an appeal quotes
      // back; translating them here would have the two conversations using different words.
      header: t(S.reasons),
      cell: (r) => <CodeList codes={r.reasonCodes} />,
    },
    {
      key: "rendered",
      header: t(S.rendered),
      // A BOOLEAN. The officer confirms the service happened without reading a single clinical field, which
      // is the whole shape of minimum-necessary in this portal.
      cell: (r) => (r.resultExists ? <StatusChip kind="ok" label={t(S.yes)} /> : <StatusChip kind="warn" label={t(S.no)} />),
      sortable: true,
      sortValue: (r) => (r.resultExists ? 0 : 1),
    },
    {
      key: "act",
      header: t(S.action),
      cell: (r) => (
        <Button size="sm" variant="secondary" onClick={() => setSelected(r.claimLineId)}>{t(S.decide)}</Button>
      ),
    },
  ];

  /* Read outside AsyncSection's render prop: a hook called in there would be conditional on the load. */
  const query = useTableQuery<AdjudicationRow>({
    rows,
    columns: cols,
    searchText: (r) => [r.claimNo, r.code, r.description, ...r.reasonCodes].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    pageSize: 25,
    persistKey: "claims-adjudication",
  });

  return (
    <>
      <PageHeader title={t(S.title)} />
      <InlineAlert tone="info">{t(S.intro)}</InlineAlert>
      <div style={{ display: "flex", flexWrap: "wrap", alignItems: "flex-end", gap: "var(--sp3)", marginBlock: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.fRec)}
          value={recommendation}
          onChange={setRecommendation}
          segments={[
            { value: "", label: t(S.fAny) },
            { value: "Approve", label: t(S.fApprove) },
            { value: "Deny", label: t(S.fDeny) },
            { value: "RequiresManualReview", label: t(S.fManual) },
          ]}
        />
        <InputField
          label={t(S.fValue)}
          value={minValue}
          inputMode="decimal"
          onChange={(e) => setMinValue(e.currentTarget.value)}
          onBlur={() => setCommittedMin(minValue.trim() === "" ? undefined : Number(minValue))}
        />
      </div>

      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
            {() => (
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.claimLineId}
                caption={t(S.title)}
                interactive
                selectedKey={selected ?? undefined}
                onSelect={(r) => setSelected(r.claimLineId)}
                emptyLabel={t(S.empty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selectedRow ? (
            <DecisionPanel
              key={selectedRow.claimLineId}
              row={selectedRow}
              t={t}
              onDone={() => { setSelected(null); state.reload(); }}
            />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}>
              <p className="muted">{t(S.pick)}</p>
            </Card>
          )}
        </div>
      </div>
    </>
  );
}

function DecisionPanel({
  row,
  t,
  onDone,
}: {
  row: AdjudicationRow;
  t: (l: Localized) => string;
  onDone: () => void;
}) {
  const api = useApi();
  const fmt = useFormat();
  const { toast } = useToast();
  const [decision, setDecision] = useState<ClaimDecisionKind>("Approve");
  const [allowedAmount, setAllowedAmount] = useState("");
  const [rationale, setRationale] = useState("");
  const [codes, setCodes] = useState<string[]>(row.reasonCodes);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [notice, setNotice] = useState<{ tone: "info" | "bad"; text: Localized } | null>(null);
  const [busy, setBusy] = useState(false);

  function toggle(code: string) {
    setCodes((prev) => (prev.includes(code) ? prev.filter((c) => c !== code) : [...prev, code]));
  }

  async function submit() {
    // Validated with the SAME schema the server enforces, so a missing rationale or a missing allowed amount
    // is refused before the round trip rather than after the reviewer has written one.
    const candidate = {
      claimId: row.claimId,
      claimLineId: row.claimLineId,
      decision,
      allowedAmount: ["PartiallyApprove", "Adjust"].includes(decision) && allowedAmount.trim() !== ""
        ? Number(allowedAmount)
        : undefined,
      reasonCodes: codes,
      rationale,
    };
    const parsed = zClaimDecisionRequest.safeParse(candidate);
    if (!parsed.success) {
      const next: Record<string, string> = {};
      for (const issue of parsed.error.issues) {
        if (issue.path[0] === "rationale") next.rationale = t(S.rationaleReq);
        if (issue.path[0] === "allowedAmount") next.allowedAmount = t(S.allowedReq);
      }
      setErrors(next);
      return;
    }
    setErrors({});
    setNotice(null);
    setBusy(true);
    try {
      const res = await api.decideClaimLine(parsed.data);
      if (res.outcome === "PendingSecondApproval") {
        // NOT an error. The decision is recorded and held; treating a 202 as a failure would teach the
        // reviewer that the dual-control threshold is a malfunction and that the way past it is to retry.
        setNotice({ tone: "info", text: S.pendingBody });
        return;
      }
      toast(t(res.outcome === "Replayed" ? S.replayed : S.recorded), "ok");
      onDone();
    } catch (e) {
      setNotice({ tone: "bad", text: refusalText(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div>
        <h2 className="section-h" style={{ marginBlockStart: 0 }}>
          <span className="tnum">{row.code}</span>{row.description ? ` · ${row.description}` : ""}
        </h2>
        <dl className="rxv-meta">
          <dt>{t(S.claimNo)}</dt>
          <dd className="tnum">{row.claimNo}</dd>
          <dt>{t(S.billed)}</dt>
          <dd className="tnum">{fmt.money(row.billedAmount)}</dd>
          <dt>{t(S.contract)}</dt>
          <dd className="tnum">{row.contractPrice == null ? "—" : fmt.money(row.contractPrice)}</dd>
          <dt>{t(S.rendered)}</dt>
          <dd>{row.resultExists ? t(S.yes) : t(S.no)}</dd>
        </dl>
      </div>

      <form className="stack" aria-label={t(S.decision)} onSubmit={(e) => { e.preventDefault(); void submit(); }}>
        <fieldset className="fieldset">
          <legend>{t(S.decision)}</legend>
          <SegmentedControl<ClaimDecisionKind>
            aria-label={t(S.decision)}
            value={decision}
            onChange={setDecision}
            segments={CLAIM_DECISION_KINDS.map((k) => ({ value: k, label: t(DECISION_LABEL[k]) }))}
          />
        </fieldset>

        {["PartiallyApprove", "Adjust"].includes(decision) && (
          <InputField
            label={t(S.allowedAmount)}
            value={allowedAmount}
            inputMode="decimal"
            onChange={(e) => setAllowedAmount(e.currentTarget.value)}
            error={errors.allowedAmount}
          />
        )}

        <fieldset className="fieldset">
          <legend>{t(S.reasonCodes)}</legend>
          <p className="muted" style={{ marginBlockStart: 0 }}>{t(S.reasonHint)}</p>
          <ul className="chip-list">
            {CLAIM_REASON_CODES.map((c) => (
              <li key={c}>
                <label className="check">
                  <input type="checkbox" checked={codes.includes(c)} onChange={() => toggle(c)} />
                  <span className="tnum">{c}</span>
                </label>
              </li>
            ))}
          </ul>
        </fieldset>

        <TextareaField
          label={t(S.rationale)}
          help={t(S.rationaleHint)}
          value={rationale}
          onChange={(e) => setRationale(e.currentTarget.value)}
          error={errors.rationale}
          rows={3}
        />

        <div aria-live="polite">
          {notice && (
            <InlineAlert tone={notice.tone}>
              {notice.tone === "info" ? <><strong>{t(S.pendingTitle)}</strong> — {t(notice.text)}</> : t(notice.text)}
            </InlineAlert>
          )}
        </div>

        <div>
          <Button type="submit" variant={decision === "Deny" ? "danger" : "primary"} loading={busy}>
            {t(S.submit)}
          </Button>
        </div>
      </form>
    </Card>
  );
}

/**
 * A refusal, said as itself.
 *
 * The server names its segregation-of-duties reason in the RFC-7807 `reason` extension, and each of the three
 * means something different about what the reviewer should do next: find a colleague who did not raise the
 * claim, hand it to someone unaffiliated with the provider, or fetch a second approver. One generic
 * "forbidden" for all three tells the reviewer only that the software is refusing them.
 */
function refusalText(e: unknown): Localized {
  if (!(e instanceof ApiError)) return S.failed;
  const reason = (e.problem as { reason?: string } | undefined)?.reason;
  if (reason === "SOD_ORIGINATOR_CANNOT_ADJUDICATE") return S.sodOriginator;
  if (reason === "SOD_PROVIDER_AFFILIATED") return S.sodProvider;
  if (reason === "SOD_SAME_DECIDER") return S.sodSameDecider;
  if (e.status === 409) return S.conflict;
  return S.failed;
}
