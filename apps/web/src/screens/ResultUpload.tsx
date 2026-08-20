import { useState } from "react";
import { Button, Card, Icon, InlineAlert, InputField, StatusChip, useTheme } from "@mersal/design-system";
import { useWrite, writeErrorText } from "../api/useWrite";
import type { Localized, ResultTask } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Upload Result", ar: "رفع النتيجة" },
  empty: { en: "No consumed lines are awaiting a result.", ar: "لا توجد بنود مُنفّذة بانتظار نتيجة." },
  patient: { en: "Patient", ar: "المريض" },
  order: { en: "Order", ar: "الطلب" },
  code: { en: "Code", ar: "الرمز" },
  resultValue: { en: "Result summary", ar: "ملخص النتيجة" },
  resultHelp: {
    en: "A structured summary or reading. The ordering clinician sees this first.",
    ar: "ملخص أو قراءة. هذا ما يراه الطبيب الطالب أولاً.",
  },
  // 32.6 — the report FILE. The service has taken one since phase 5 and stored it through document-service;
  // the screen sent only the summary and told the operator that report files "upload from the workstation",
  // which named a workflow that does not exist. For radiology the report IS the result.
  reportFile: { en: "Report file (optional)", ar: "ملف التقرير (اختياري)" },
  reportHelp: {
    en: "The signed report or image, if you have one. Either the summary or the file is enough on its own.",
    ar: "التقرير الموقّع أو الصورة، إن وُجد. يكفي أحدهما — الملخص أو الملف.",
  },
  submit: { en: "Upload result", ar: "رفع النتيجة" },
  uploaded: { en: "Result uploaded — routed to the ordering clinician.", ar: "تم رفع النتيجة — أُرسلت إلى الطبيب الطالب." },
  needValue: {
    en: "Enter a result summary, attach a report file, or both.",
    ar: "أدخل ملخص النتيجة، أو أرفق ملف التقرير، أو كليهما.",
  },
} satisfies Record<string, Localized>;

/** Result-upload worklist for a lab/imaging provider — the lines they consumed and still owe a result on. */
export function ResultUpload({ kind }: { kind: "lab" | "radiology" }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ResultTask[]>(() => api.awaitingResult(kind), [kind]);
  return (
    <>
      <PageHeader title={t(S.title)} />
      <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
        {(rows) => (
          <div className="stack" style={{ gap: "var(--sp3)" }}>
            {rows.map((task) => (
              <ResultCard key={`${task.orderId}:${task.lineId}`} task={task} onDone={state.reload} />
            ))}
          </div>
        )}
      </AsyncSection>
    </>
  );
}

function ResultCard({ task, onDone }: { task: ResultTask; onDone: () => void }) {
  const api = useApi();
  const t = useLoc();
  const [value, setValue] = useState("");
  const [report, setReport] = useState<File | null>(null);
  const [status, setStatus] = useState<"idle" | "saving" | "done" | "empty">("idle");
  // 18.D1 (U1) — a result upload is a clinical write that previously failed SILENTLY and carried no
  // idempotency key: the spinner stopped, nothing appeared, and pressing the button again filed the result
  // a second time against the same order line.
  const write = useWrite();
  const { lang } = useTheme();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    // The SERVICE's rule, not a stricter one: a summary and/or a file. Requiring the summary — which is what
    // this did — is why a radiographer with a signed report and nothing to type had to invent a sentence.
    if (value.trim() === "" && report === null) { setStatus("empty"); return; }
    setStatus("saving");
    const ok = await write.run((key) =>
      api.uploadResult(task.orderId, task.lineId, { value: value.trim(), report: report ?? undefined }, key));
    if (ok) {
      setStatus("done");
      setTimeout(onDone, 800);
    } else {
      setStatus("idle");
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp4)", display: "grid", gap: "var(--sp3)" }}>
      <dl className="kv-grid" aria-label={t(S.order)}>
        <div><dt>{t(S.order)}</dt><dd className="tnum">{task.orderNo}</dd></div>
        <div><dt>{t(S.patient)}</dt><dd className="tnum">{task.beneficiary.token}</dd></div>
        <div><dt>{t(S.code)}</dt><dd className="tnum">{task.code}</dd></div>
      </dl>
      {status === "done" ? (
        <StatusChip kind="ok" label={t(S.uploaded)} />
      ) : (
        <form onSubmit={submit} className="stack" style={{ gap: "var(--sp2)" }}>
          <InputField
            label={t(S.resultValue)}
            help={t(S.resultHelp)}
            value={value}
            onChange={(e) => setValue(e.currentTarget.value)}
          />
          <div className="mrs-field">
            <label className="mrs-label" htmlFor={`report-${task.orderId}-${task.lineId}`}>
              {t(S.reportFile)}
            </label>
            <input
              id={`report-${task.orderId}-${task.lineId}`}
              className="mrs-control"
              type="file"
              accept=".pdf,.png,.jpg,.jpeg,.webp,.tif,.tiff,.dcm"
              onChange={(e) => setReport(e.currentTarget.files?.[0] ?? null)}
            />
            <p className="muted" style={{ margin: 0 }}>{t(S.reportHelp)}</p>
          </div>
          <div aria-live="polite">
            {status === "empty" && <InlineAlert tone="bad">{t(S.needValue)}</InlineAlert>}
            {/* 18.D1 (U1/U2): the typed, translated failure. InlineAlert tone="bad" carries role="alert",
                so a screen-reader user hears it without moving focus. */}
            {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
          </div>
          <div>
            <Button type="submit" variant="primary"
              leadingIcon={<Icon name="check2" />} loading={status === "saving"}>{t(S.submit)}</Button>
          </div>
        </form>
      )}
    </Card>
  );
}
