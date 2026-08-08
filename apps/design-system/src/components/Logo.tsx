import logoLockup from "../assets/mersal-logo.svg";
import logoLockupDark from "../assets/mersal-logo-dark.svg";
import { cx } from "../lib/cx";
import { useTheme } from "../theme/ThemeProvider";

export interface LogoProps {
  /** `lockup` = full Mersal Foundation wordmark (auth screen, large). `mark` = compact tile (nav rail). */
  variant?: "lockup" | "mark";
  /** Optional app suffix shown beside the mark (e.g. "HBMP"). */
  wordmark?: string;
  className?: string;
  /** Height in px for the lockup variant. */
  height?: number;
  /**
   * Render the DARK-surface lockup regardless of the active theme (28.8).
   *
   * The asset is chosen by theme because, until now, every surface the lockup sat on followed the theme. The
   * login hero does not: it is a deep teal panel in BOTH themes, and the light lockup's teal wordmark sits at
   * about 2:1 on it — the same illegibility the dark asset was created to fix, arriving from the other
   * direction. This is a per-SURFACE choice, which is what the two assets were always really about.
   */
  onDark?: boolean;
}

/**
 * Mersal brand lockup (0B §8). The official Mersal Foundation logo (gold Arabic مرسال over teal "Mersal",
 * "FOUNDATION" beneath) is rendered from a scalable SVG; a text fallback ("Mersal") keeps the shell
 * from breaking if the asset fails to load. RTL/LTR both supported — the mark itself is direction-neutral.
 */
export function Logo({ variant = "mark", wordmark, className, height = 44, onDark }: LogoProps) {
  const { theme } = useTheme();
  if (variant === "lockup") {
    return (
      <span className={cx("mrs-brand", className)}>
        {/*
          * Two assets, one per surface (0B §8; audit §5.5).
          *
          * The light lockup's Latin wordmark is the brand teal and its sub-wordmark is slate — both picked
          * against white, and both sitting at around 2:1 on the dark surface. It was the sign-in screen's
          * least legible element, which is the first thing anyone sees of this product. Recolouring in CSS
          * was not available: these are `<text fill="…">` inside an `<img>`, so the page cannot reach them.
          */}
        <img
          src={onDark || theme === "dark" ? logoLockupDark : logoLockup}
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
