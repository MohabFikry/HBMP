import { z } from "zod";
import { zId, zInstant, zStatus } from "./common";

/**
 * Notification inbox contracts (Phase 8.1, US-072). The in-app inbox is strictly self-service — the service
 * row-filters by recipient == caller, so these schemas carry no recipient identity. Min-necessary: a row carries
 * a subject/body + a min-necessary business key (entityRef), never clinical content. Every status renders as a
 * non-color StatusKind chip (accessibility). The subject/body are pre-localized server-side (the caller's locale).
 */
export const zNotification = z.object({
  id: zId,
  subject: z.string(),
  body: z.string(),
  status: zStatus,
  /** Min-necessary business key the notification points at, e.g. "AUTH-2026-0001" (never PHI). */
  entityRef: z.string().optional(),
  /** The domain event that produced the notification, e.g. "AuthorizationDecided". */
  sourceEventType: z.string(),
  /** True when the item is actionable and still needs the recipient to act. */
  actionable: z.boolean(),
  read: z.boolean(),
  createdAt: zInstant,
});
export type Notification = z.infer<typeof zNotification>;

/** Result of marking a notification read (also stops its escalation timer server-side). */
export const zMarkReadResult = z.object({
  id: zId,
  read: z.literal(true),
});
export type MarkReadResult = z.infer<typeof zMarkReadResult>;

/** Result of clearing the caller's unread inbox — how many notifications the call actually marked. */
export const zMarkAllReadResult = z.object({
  marked: z.number().int().nonnegative(),
});
export type MarkAllReadResult = z.infer<typeof zMarkAllReadResult>;
