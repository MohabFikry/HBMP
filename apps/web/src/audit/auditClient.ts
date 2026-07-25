/**
 * Front-end audit seam. Security-relevant UI events — notably a forbidden deep-link attempt (US-071) and
 * sign-in/out — are emitted here. In dev they are recorded in-memory + logged; in production this posts to
 * audit-service (the same append-only, hash-chained spine as the backend). The server also audits the API
 * calls themselves; this captures the *navigation* attempt the server would otherwise never see.
 */
export interface AccessAuditEvent {
  type: "access.denied" | "auth.login" | "auth.logout" | "auth.timeout";
  actorUserId: string | null;
  actorRole: string | null;
  /** The route the user attempted (for access.denied). */
  path?: string;
  reason?: string;
  at: string;
}

const buffer: AccessAuditEvent[] = [];

export const auditClient = {
  emit(event: Omit<AccessAuditEvent, "at">): void {
    const full: AccessAuditEvent = { ...event, at: new Date().toISOString() };
    buffer.push(full);
    // Dev sink; replaced by an audit-service POST in production wiring.
    if (typeof console !== "undefined") console.info("[audit]", full.type, full.path ?? "", full.reason ?? "");
  },
  /** Test/introspection helper. */
  drain(): AccessAuditEvent[] {
    return buffer.splice(0, buffer.length);
  },
  peek(): readonly AccessAuditEvent[] {
    return buffer;
  },
};
