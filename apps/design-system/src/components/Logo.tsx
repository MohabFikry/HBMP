import logoLockup from "../assets/mersal-logo.svg";
import { cx } from "../lib/cx";

export interface LogoProps {
  /** `lockup` = full Mersal Foundation wordmark (auth screen, large). `mark` = compact tile (nav rail). */
  variant?: "lockup" | "mark";
  /** Optional app suffix shown beside the mark (e.g. "HBMP"). */
  wordmark?: string;
  className?: string;
  /** Height in px for the lockup variant. */
  height?: number;
}

/**
 * Mersal brand lockup (0B §8). The official Mersal Foundation logo (gold Arabic مرسال over teal "Mersal",
 * "FOUNDATION" beneath) is rendered from a scalable SVG; a text fallback ("Mersal") keeps the shell
 * from breaking if the asset fails to load. RTL/LTR both supported — the mark itself is direction-neutral.
 */
export function Logo({ variant = "mark", wordmark, className, height = 44 }: LogoProps) {
  if (variant === "lockup") {
    return (
      <span className={cx("mrs-brand", className)}>
        <img
          src={logoLockup}
          alt="Mersal Foundation"
          height={height}
          style={{ height, width: "auto", display: "block" }}
        />
      </span>
    );
  }
  return (
    <span className={cx("mrs-brand", className)}>
      <span className="mrs-logo" aria-hidden="true">
        {/* Compact tile: gold Arabic "م" on the teal brand tile, matching the official mark. */}
        <svg viewBox="0 0 64 64" width={38} height={38} role="img" aria-label="Mersal">
          <rect width="64" height="64" rx="16" fill="#16808D" />
          <text
            x="32"
            y="46"
            textAnchor="middle"
            fontFamily="Cairo, 'Noto Sans Arabic', system-ui, sans-serif"
            fontWeight="600"
            fontSize="42"
            fill="#E0A106"
          >
            م
          </text>
        </svg>
      </span>
      {wordmark ? (
        <span className="mrs-wordmark">
          Mersal <span style={{ color: "var(--text-2)", fontWeight: 500 }}>{wordmark}</span>
        </span>
      ) : (
        <span className="mrs-wordmark sr-only">Mersal Foundation</span>
      )}
    </span>
  );
}
