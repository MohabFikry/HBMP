import { useState } from "react";
import { Button, Card, DataTable, StatusChip, type Column } from "@mersal/design-system";
import type { Localized, ReportAccessRequestRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

/**
 * Phase 18.C2 (audit R2 W4) — the approver inbox for sensitive-result release requests (design 37 §6).
 *
 * The whole request/grant workflow shipped in 14.7 with no way to SEE a request. A clinician could raise one,
 * and the endpoint that decides it takes an id — an id nothing displayed. So the sensitive-result gate was
 * permanent-deny in practice: every request sat in a table until it expired, and the clinician on the other
 * end simply never got an answer. That is the worst failure mode for a break-glass-adjacent control, because
 * the pressure it creates is to route around it.
 *
 * The screen is deliberately CLINICAL-FREE. It shows who asked, for which order line, under what purpose and
 * why — never the result. An approver is deciding whether the REQUESTER may see it; showing it to them here
 * would disclose the exact thing being gated to everyone who can open the inbox.
 */
const S = {
  title: { en: "Result access requests", ar: "طلبات الوصول إلى النتائج" },
  empty: { en: "No requests awaiting a decision.", ar: "لا توجد طلبات بانتظار القرار." },
  requester: { en: "Requested by", ar: "مقدّم الطلب" },
  member: { en: "Member", ar: "المستفيد" },
  purpose: { en: "Purpose", ar: "الغرض" },
  justification: { en: "Justification", ar: "المبرر" },
  ttl: { en: "Requested for", ar: "المدة المطلوبة" },
  status: { en: "Status", ar: "الحالة" },
  raised: { en: "Raised", ar: "تاريخ الطلب" },
  actions: { en: "Decision", ar: "القرار" },
  approve: { en: "Approve", ar: "موافقة" },
  deny: { en: "Deny", ar: "رفض" },
  askInfo: { en: "Ask for more", ar: "طلب إيضاح" },
  reasonLabel: { en: "Reason for this decision (recorded in the audit trail)", ar: "سبب القرار (يُسجَّل في سجل التدقيق)" },
  reasonRequired: { en: "A reason is required — it is recorded against the beneficiary's record.", ar: "المبرر مطلوب — يُسجَّل في ملف المستفيد." },
  hours: { en: "hours", ar: "ساعة" },
  cappedNote: {
    en: "The granted window may be shorter than requested — policy caps it by the result's sensitivity.",
    ar: "قد تكون المدة الممنوحة أقصر من المطلوبة — تحددها حساسية النتيجة.",
  },
  failed: { en: "The decision could not be recorded. Nothing was changed — please try again.", ar: "تعذّر تسجيل القرار. لم يتم تغيير أي شيء — يرجى المحاولة مرة أخرى." },
} satisfies Record<string, Localized>;

type Decision = "approve" | "deny" | "requestinfo";

export function ReportAccessInbox() {
  const api = useApi();
  const t = useLoc();
  const [reloadKey, setReloadKey] = useState(0);
  const state = useAsync<ReportAccessRequestRow[]>(() => api.reportAccessInbox(), [reloadKey]);
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function decide(row: ReportAccessRequestRow, decision: Decision) {
    const reason = (reasons[row.requestId] ?? "").trim();
    // A decision on someone's clinical record is not a bare button press. The reason is required BEFORE the
    // call, not validated by the server afterwards, so the approver is never told "invalid" about something
    // they were allowed to submit.
    if (!reason) {
      setError(t(S.reasonRequired));
      return;
    }
    setBusy(row.requestId);
    setError(null);
    try {
      await api.decideReportAccess(row.requestId, decision, reason, row.requestedTtlHours);
      setReloadKey((k) => k + 1);
    } catch {
      setError(t(S.failed));
    } finally {
      setBusy(null);
    }
  }

  const cols: Column<ReportAccessRequestRow>[] = [
    { key: "requester", header: t(S.requester), cell: (r) => <span>{r.requestedBy}{r.requestedForRole ? ` · ${r.requestedForRole}` : ""}</span> },
    { key: "member", header: t(S.member), cell: (r) => <span className="tnum">{r.beneficiaryToken}</span> },
    { key: "purpose", header: t(S.purpose), cell: (r) => <StatusChip kind="info" label={r.purposeCode} /> },
    { key: "justification", header: t(S.justification), cell: (r) => <span>{r.justification}</span> },
    { key: "ttl", header: t(S.ttl), cell: (r) => <span className="tnum">{r.requestedTtlHours ? `${r.requestedTtlHours} ${t(S.hours)}` : "—"}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "raised", header: t(S.raised), cell: (r) => <span className="tnum">{new Date(r.createdAt).toLocaleString()}</span> },
    {
      key: "actions",
      header: t(S.actions),
      cell: (r) => (
        <div style={{ display: "grid", gap: "var(--sp2)", minWidth: "18rem" }}>
          <label htmlFor={`reason-${r.requestId}`} style={{ fontSize: "0.85rem" }}>{t(S.reasonLabel)}</label>
          <input
            id={`reason-${r.requestId}`}
            value={reasons[r.requestId] ?? ""}
            onChange={(e) => setReasons((m) => ({ ...m, [r.requestId]: e.target.value }))}
            style={{ minHeight: 44 }}
          />
          <div style={{ display: "flex", gap: "var(--sp2)", flexWrap: "wrap" }}>
            <Button onClick={() => void decide(r, "approve")} disabled={busy === r.requestId}>{t(S.approve)}</Button>
            <Button variant="secondary" onClick={() => void decide(r, "deny")} disabled={busy === r.requestId}>{t(S.deny)}</Button>
            <Button variant="ghost" onClick={() => void decide(r, "requestinfo")} disabled={busy === r.requestId}>{t(S.askInfo)}</Button>
          </div>
        </div>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted" style={{ marginTop: 0 }}>{t(S.cappedNote)}</p>
        {/* aria-live so a screen-reader user hears the outcome; the table below re-renders silently. */}
        <p role="alert" aria-live="polite" style={{ color: "var(--color-danger-fg, #b91c1c)" }}>
          {error ?? ""}
        </p>
        <AsyncSection<ReportAccessRequestRow[]> state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.requestId} caption={t(S.title)} />}
        </AsyncSection>
      </Card>
    </>
  );
}
