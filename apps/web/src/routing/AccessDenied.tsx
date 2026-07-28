import { Button, Card, Icon, InlineAlert, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";

/**
 * Phase 21.6 — the THREE distinct 403 treatments (design 40 §4 and §6).
 *
 * All three are HTTP 403, and it would be simpler to render one page for all of them. That is exactly the
 * mistake: the remedies are different people.
 *
 *   • permission denial      → ask YOUR administrator
 *   • programme not enabled  → ask MERSAL programme administration
 *   • branch out of scope    → you can fix it yourself, by switching branch
 *
 * A single generic page sends two out of three users to someone who cannot help them, and (under A4) makes
 * an onboarding gap read like a permission problem to a partner NGO. So the treatment is selected from the
 * problem `type` the API returns — never from the status code, which is identical in all three cases.
 */
export type AccessDeniedKind = "forbidden" | "program-not-enabled" | "program-limit-reached" | "branch-out-of-scope";

/** Map an RFC-7807 `type` onto a treatment. Anything unrecognised falls back to the permission denial,
 *  which is the safe default: it never claims the platform is at fault when we do not know. */
export function kindFromProblemType(type: string | undefined): AccessDeniedKind {
  if (!type) return "forbidden";
  if (type.includes("program-not-enabled")) return "program-not-enabled";
  if (type.includes("program-limit-reached")) return "program-limit-reached";
  if (type.includes("branch-out-of-scope")) return "branch-out-of-scope";
  return "forbidden";
}

export interface AccessDeniedProps {
  kind: AccessDeniedKind;
  /** The feature or limit key the API named, so the copy can be specific rather than generic. */
  detailKey?: string;
  /** Branch switcher, supplied by the shell for the branch-out-of-scope case. */
  onSwitchBranch?: () => void;
  onRequestAccess?: () => void;
  onBack?: () => void;
}

/**
 * Four-cue status per 0B and 21-accessibility: the tone is never carried by colour alone — each treatment
 * has its own icon, its own heading text, and a distinct `data-treatment` hook the tests assert on.
 */
export function AccessDenied({ kind, detailKey, onSwitchBranch, onRequestAccess, onBack }: AccessDeniedProps) {
  const { lang } = useTheme();

  const copy = {
    "forbidden": {
      icon: "cross" as const,
      title: L.forbiddenTitle[lang],
      body: L.forbiddenBody[lang],
      tone: "bad" as const,
    },
    "program-not-enabled": {
      icon: "info" as const,
      title: L.notEnabledTitle[lang],
      body: L.notEnabledBody[lang],
      tone: "info" as const,
    },
    "program-limit-reached": {
      icon: "info" as const,
      title: L.limitReachedTitle[lang],
      body: L.limitReachedBody[lang],
      tone: "warn" as const,
    },
    "branch-out-of-scope": {
      icon: "info" as const,
      title: L.branchOutOfScopeTitle[lang],
      body: L.branchOutOfScopeBody[lang],
      tone: "warn" as const,
    },
  }[kind];

  return (
    <Card
      role="region"
      aria-label={copy.title}
      data-treatment={kind}
      style={{ padding: "var(--sp6)", maxWidth: 560, margin: "var(--sp8) auto" }}
    >
      <div style={{ display: "flex", gap: "var(--sp3)", alignItems: "center", marginBottom: "var(--sp3)" }}>
        <Icon name={copy.icon} width={28} height={28} aria-hidden />
        <h1 style={{ fontSize: "var(--fs-title-1)" }}>{copy.title}</h1>
      </div>

      <p className="muted">{copy.body}</p>

      {detailKey ? (
        <InlineAlert tone={copy.tone}>
          <span className="mono">{detailKey}</span>
        </InlineAlert>
      ) : null}

      <div style={{ display: "flex", gap: "var(--sp3)", marginTop: "var(--sp5)", flexWrap: "wrap" }}>
        {/* The action differs per treatment — that IS the separation, expressed as an affordance. */}
        {kind === "forbidden" && onRequestAccess ? (
          <Button variant="primary" onClick={onRequestAccess}>
            {L.requestAccess[lang]}
          </Button>
        ) : null}

        {(kind === "program-not-enabled" || kind === "program-limit-reached") ? (
          <Button
            variant="primary"
            onClick={() => window.open("mailto:programmes@mersal.foundation", "_blank", "noopener")}
          >
            {L.contactMersal[lang]}
          </Button>
        ) : null}

        {kind === "branch-out-of-scope" && onSwitchBranch ? (
          <Button variant="primary" onClick={onSwitchBranch}>
            {L.switchBranch[lang]}
          </Button>
        ) : null}

        {onBack ? (
          <Button variant="secondary" onClick={onBack}>
            {L.backToPortal[lang]}
          </Button>
        ) : null}
      </div>
    </Card>
  );
}
