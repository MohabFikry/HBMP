import type { Section } from "../portals/catalog";

/**
 * Resolve the portal section a notification originates from, so the sliding pane can deep-link the
 * recipient to where they take action or get more detail (US-072). Notifications are role-scoped
 * (the service row-filters by recipient == caller), so we only ever match against the CURRENT user's
 * accessible sections — the link can never point at a section the user isn't permitted to open
 * (min-necessary). Returns the matched section, or `null` when nothing fits (the item then just
 * opens the full inbox instead of a targeted section).
 *
 * The match keys off the notification's `sourceEventType` (e.g. `AuthorizationDecided`,
 * `OrderLineConsumed`, `EscalationRaised`) — a coarse domain heuristic, not an exhaustive registry,
 * so a new event type degrades gracefully to the inbox rather than breaking.
 */
const EVENT_SECTION_KEYWORDS: Array<[RegExp, readonly string[]]> = [
  // Approvals / authorization lifecycle → the approver's worklist, or the ordering clinician's lists.
  [/auth/i, ["worklist", "sla", "manual", "emergency", "orders", "prescriptions"]],
  [/escalat/i, ["escalations", "sla"]],
  // Investigation orders + lab/imaging fulfillment.
  [/order|consume|fulfil/i, ["awaiting", "consume", "result", "orders", "queue"]],
  [/result/i, ["results", "result", "awaiting"]],
  // Prescriptions + pharmacy dispensing.
  [/rx|prescription|dispense|formulary|substitut/i, ["prescriptions", "dispense", "substitutions", "queue"]],
  // Referrals + case coordination.
  [/referral/i, ["directory", "referrals", "my-cases"]],
  [/case/i, ["my-cases", "escalations"]],
  // Appointments / reception desk.
  [/appointment|slot|no-?show|reschedul|checkin|check-in/i, ["appointments", "check-in", "queue"]],
  // Governance / admin.
  [/break-?glass|access-?review|sod|policy|policies|tenant|role|session|device/i, ["audit", "policies", "tenants", "users", "config", "master-data"]],
  // Beneficiary / eligibility / coverage.
  [/coverage|eligib|beneficiary|member|register/i, ["eligibility", "search", "manage", "status", "register"]],
  // Finance / settlements.
  [/settlement|invoice|payment|finance|utilization/i, ["settlements", "utilization", "summaries", "exports"]],
];

export function originSection(sourceEventType: string, sections: Section[]): Section | null {
  for (const [rx, keywords] of EVENT_SECTION_KEYWORDS) {
    if (!rx.test(sourceEventType)) continue;
    const hit = sections.find((s) => keywords.some((k) => s.path.includes(k)));
    if (hit) return hit;
  }
  return null;
}
