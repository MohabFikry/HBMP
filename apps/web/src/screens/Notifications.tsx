import { useState } from "react";
import { Button, Card, DataTable, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, Notification } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Notifications", ar: "الإشعارات" },
  empty: { en: "You're all caught up — no notifications.", ar: "لا توجد إشعارات جديدة." },
  subject: { en: "Notification", ar: "الإشعار" },
  ref: { en: "Reference", ar: "المرجع" },
  status: { en: "Status", ar: "الحالة" },
  received: { en: "Received", ar: "وقت الاستلام" },
  action: { en: "Action", ar: "إجراء" },
  markRead: { en: "Mark read", ar: "تحديد كمقروء" },
  read: { en: "Read", ar: "مقروء" },
  unreadOnly: { en: "Unread only", ar: "غير المقروءة فقط" },
  all: { en: "All", ar: "الكل" },
} satisfies Record<string, Localized>;

/**
 * The caller's own in-app inbox (Phase 8.1, US-072). Cross-portal: every role has an inbox, row-filtered
 * server-side by recipient == caller (inherently min-necessary — no recipient identity, no clinical content;
 * only a subject/body + a min-necessary business key). Marking an item read also stops its escalation timer.
 */
export function Notifications() {
  const api = useApi();
  const t = useLoc();
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const state = useAsync<Notification[]>(() => api.notifications(unreadOnly), [unreadOnly]);

  async function markRead(id: string) {
    setBusy(id);
    try {
      await api.markNotificationRead(id);
      state.reload();
    } finally {
      setBusy(null);
    }
  }

  const cols: Column<Notification>[] = [
    {
      key: "subject",
      header: t(S.subject),
      cell: (r) => (
        <div className="stack" style={{ gap: "2px" }}>
          <strong style={{ fontWeight: r.read ? 400 : 700 }}>{r.subject}</strong>
          <span className="muted">{r.body}</span>
        </div>
      ),
    },
    { key: "ref", header: t(S.ref), cell: (r) => (r.entityRef ? <span className="tnum">{r.entityRef}</span> : <span className="muted">—</span>) },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "received", header: t(S.received), cell: (r) => <span className="tnum">{new Date(r.createdAt).toLocaleString()}</span> },
    {
      key: "action",
      header: t(S.action),
      cell: (r) =>
        r.read ? (
          <StatusChip kind="neu" label={t(S.read)} />
        ) : (
          <Button size="sm" variant="secondary" disabled={busy === r.id} onClick={() => markRead(r.id)}>
            {t(S.markRead)}
          </Button>
        ),
    },
  ];

  return (
    <>
      <PageHeader
        title={t(S.title)}
        actions={
          <Button variant={unreadOnly ? "primary" : "secondary"} size="sm" onClick={() => setUnreadOnly((v) => !v)}>
            {unreadOnly ? t(S.all) : t(S.unreadOnly)}
          </Button>
        }
      />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.title)} />}
        </AsyncSection>
      </Card>
    </>
  );
}
