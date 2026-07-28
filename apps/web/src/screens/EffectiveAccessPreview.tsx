import { useMemo } from "react";
import { Card, InlineAlert, useTheme } from "@mersal/design-system";

/**
 * Phase 21.6 — the effective-access PREVIEW (design 40 §5, §6).
 *
 * "What can this person actually do, and WHY." The why is the part that matters: an administrator looking
 * at a flat list of keys cannot tell a role grant from a hand-written exception, so they cannot review it.
 * Each key is annotated with its provenance — role, override, or denied-by — which is what turns the list
 * from data into evidence.
 *
 * It renders the MODE-2 evaluator's output verbatim. It deliberately does NOT recompute the algebra in the
 * browser: a second implementation would be a third opinion about who can do what, and the parity suite
 * only covers the two on the server. Whatever the API says is what is shown.
 */
export interface EffectiveAccessKey {
  key: string;
  /** Where the key came from. `denied` means a Deny override removed a key the roles DO grant. */
  source: "role" | "override" | "platform-admin" | "denied";
  /** The role or the override's grantor, for display. */
  via?: string;
  /** Present when the key is superseded — rendered muted with its pointer (design 40 §6). */
  deprecated?: boolean;
  replacedBy?: string | null;
  /** The override's reason, which is what makes an exception reviewable. */
  reason?: string;
}

export interface EffectiveAccessPreviewProps {
  membershipId: string;
  keys: EffectiveAccessKey[];
  /** True while the mode-2 call is in flight. */
  loading?: boolean;
  error?: string;
}

const SOURCE_LABEL: Record<EffectiveAccessKey["source"], { en: string; ar: string }> = {
  "role": { en: "from role", ar: "من الدور" },
  "override": { en: "override", ar: "استثناء" },
  "platform-admin": { en: "platform administration", ar: "إدارة المنصّة" },
  "denied": { en: "denied by override", ar: "ممنوع باستثناء" },
};

export function EffectiveAccessPreview({ membershipId, keys, loading, error }: EffectiveAccessPreviewProps) {
  const { lang } = useTheme();

  // Denied keys are listed WITH the granted ones rather than filtered out. "orders:read — denied by
  // override, because X" is the single most useful line on this screen: it explains an absence that would
  // otherwise look like a bug in the role definition and send someone re-granting the role.
  const sorted = useMemo(
    () => [...keys].sort((a, b) => a.key.localeCompare(b.key, "en")),
    [keys],
  );

  if (loading) return <Card style={{ padding: "var(--sp5)" }}><p className="muted">…</p></Card>;
  if (error) return <InlineAlert tone="bad">{error}</InlineAlert>;

  return (
    <Card
      role="region"
      aria-label={lang === "ar" ? "الصلاحيات الفعلية" : "Effective access"}
      data-membership={membershipId}
      style={{ padding: "var(--sp5)" }}
    >
      <table style={{ width: "100%", borderCollapse: "collapse" }}>
        <caption className="muted" style={{ textAlign: "start", marginBottom: "var(--sp3)" }}>
          {lang === "ar"
            ? "كل صلاحية ومصدرها — الأدوار والاستثناءات ومبرراتها."
            : "Every key and where it comes from — roles, overrides, and the reasons behind them."}
        </caption>
        <thead>
          <tr>
            <th scope="col" style={{ textAlign: "start" }}>{lang === "ar" ? "الصلاحية" : "Key"}</th>
            <th scope="col" style={{ textAlign: "start" }}>{lang === "ar" ? "المصدر" : "Source"}</th>
            <th scope="col" style={{ textAlign: "start" }}>{lang === "ar" ? "السبب" : "Reason"}</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((k) => (
            <tr key={k.key} data-source={k.source} data-deprecated={k.deprecated ? "true" : undefined}>
              <td>
                {/* A deprecated key still WORKS (deprecation is a migration signal, not enforcement), so it
                    is shown muted with its replacement rather than hidden — hiding it would leave an
                    administrator unable to see what they still have to migrate off. */}
                <span className={k.deprecated ? "muted mono" : "mono"}>{k.key}</span>
                {k.deprecated ? (
                  <span className="muted"> → {k.replacedBy ?? (lang === "ar" ? "بلا بديل" : "no replacement")}</span>
                ) : null}
              </td>
              <td>
                {/* Text, not a colour chip alone — four-cue status (21-accessibility). */}
                <span>{SOURCE_LABEL[k.source][lang]}</span>
                {k.via ? <span className="muted"> · {k.via}</span> : null}
              </td>
              <td className="muted">{k.reason ?? ""}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {sorted.length === 0 ? (
        <InlineAlert tone="info">
          {lang === "ar"
            ? "لا توجد صلاحيات فعلية لهذه العضوية."
            : "This membership currently has no effective access."}
        </InlineAlert>
      ) : null}
    </Card>
  );
}
