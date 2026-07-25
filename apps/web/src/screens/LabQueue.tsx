import { useState } from "react";
import { Button, Card, DataTable, InputField, Modal, StatusChip, useToast } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { LabOrder, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { newIdempotencyKey } from "../api/http";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  labTitle: { en: "Lab order queue", ar: "قائمة طلبات المختبر" },
  imagingTitle: { en: "Imaging order queue", ar: "قائمة طلبات الأشعة" },
  empty: { en: "No orders in the queue.", ar: "لا توجد طلبات في الطابور." },
  test: { en: "Test", ar: "الفحص" },
  patient: { en: "Patient", ar: "المريض" },
  priority: { en: "Priority", ar: "الأولوية" },
  progress: { en: "Progress", ar: "التقدّم" },
  state: { en: "State", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  consume: { en: "Consume", ar: "تنفيذ" },
  consumeTitle: { en: "Consume order", ar: "تنفيذ الطلب" },
  panels: { en: "Panels to fulfil now", ar: "عدد الأجزاء المنفَّذة الآن" },
  submit: { en: "Confirm consume", ar: "تأكيد التنفيذ" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  done: { en: "Order fulfilled.", ar: "تم تنفيذ الطلب." },
  partial: { en: "Order partially fulfilled.", ar: "تم تنفيذ الطلب جزئياً." },
  replay: { en: "Already recorded (idempotent replay) — no double-apply.", ar: "مُسجّل مسبقاً (إعادة متكافئة) — دون ازدواج." },
  fail: { en: "Consume failed.", ar: "فشل التنفيذ." },
} satisfies Record<string, Localized>;

const PRIORITY_KIND = { routine: "neu", urgent: "warn", emergency: "bad" } as const;

export function LabQueue({ kind }: { kind: "lab" | "imaging" }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const q = useAsync<LabOrder[]>(() => api.labQueue(kind), [kind]);
  const [active, setActive] = useState<LabOrder | null>(null);
  const [panels, setPanels] = useState(1);
  const [busy, setBusy] = useState(false);

  async function consume() {
    if (!active) return;
    setBusy(true);
    try {
      const res = await api.consume({ orderId: active.id, idempotencyKey: newIdempotencyKey(), panels });
      if (res.replayed) toast(t(S.replay), "info");
      else toast(t(res.panelsDone >= res.panelsTotal ? S.done : S.partial), "ok");
      setActive(null);
      q.reload();
    } catch {
      toast(t(S.fail), "bad");
    } finally {
      setBusy(false);
    }
  }

  const cols: Column<LabOrder>[] = [
    { key: "test", header: t(S.test), cell: (r) => <span><span className="tnum">{r.test.code}</span> · {t(r.test.label)}</span> },
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span> },
    { key: "priority", header: t(S.priority), cell: (r) => <StatusChip kind={PRIORITY_KIND[r.priority]} label={r.priority} /> },
    { key: "progress", header: t(S.progress), cell: (r) => <span className="tnum">{r.panelsDone}/{r.panelsTotal}</span> },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "action",
      header: t(S.action),
      cell: (r) => (
        <Button
          size="sm"
          variant="primary"
          disabled={r.panelsDone >= r.panelsTotal}
          onClick={() => {
            setActive(r);
            setPanels(Math.max(1, r.panelsTotal - r.panelsDone));
          }}
        >
          {t(S.consume)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(kind === "lab" ? S.labTitle : S.imagingTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={q} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => (
            <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(kind === "lab" ? S.labTitle : S.imagingTitle)} />
          )}
        </AsyncSection>
      </Card>

      <Modal
        open={active !== null}
        onOpenChange={(o) => !o && setActive(null)}
        title={t(S.consumeTitle)}
        description={active ? `${active.test.code} · ${t(active.test.label)} — ${active.patient.token}` : undefined}
        footer={
          <>
            <Button variant="ghost" onClick={() => setActive(null)}>{t(S.cancel)}</Button>
            <Button variant="primary" loading={busy} onClick={() => void consume()}>{t(S.submit)}</Button>
          </>
        }
      >
        {active && (
          <InputField
            label={t(S.panels)}
            type="number"
            min={1}
            max={active.panelsTotal - active.panelsDone}
            value={panels}
            onChange={(e) => setPanels(Number(e.currentTarget.value))}
          />
        )}
      </Modal>
    </>
  );
}
