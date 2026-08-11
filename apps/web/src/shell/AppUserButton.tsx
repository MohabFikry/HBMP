import { forwardRef } from "react";

/**
 * The person, in the app bar: initials, name, and the line under it.
 *
 * <p>ONE component, used by the portal shell and by the portal picker. They are the same control in the same
 * place doing the same thing, and the picker previously had its own arrangement — a logo, then three loose
 * buttons — so the first screen anybody saw was the one that did not look like the product. A second copy
 * here would drift the same way: the avatar sizing, the two-line stack and the truncation are the parts that
 * make it read as one control rather than two labels, and none of them is obvious enough to survive being
 * reimplemented.</p>
 */

/** Two-letter initials for the avatar placeholder. */
export function initials(name: string): string {
  const p = name.trim().split(/\s+/).filter(Boolean);
  if (p.length === 0) return "?";
  if (p.length === 1) return p[0].slice(0, 2).toUpperCase();
  return (p[0][0] + p[p.length - 1][0]).toUpperCase();
}

export const AppUserButton = forwardRef<HTMLButtonElement, {
  displayName: string;
  /**
   * The line under the name — the person's POSITION where one is recorded, and the portal's own label where
   * none is. The caller decides, because only it knows which fallback is available.
   */
  secondary: string;
  expanded: boolean;
  label: string;
  onClick: () => void;
}>(function AppUserButton({ displayName, secondary, expanded, label, onClick }, ref) {
  return (
    <button
      ref={ref}
      type="button"
      className="app-userbtn"
      aria-haspopup="dialog"
      aria-expanded={expanded}
      onClick={onClick}
      aria-label={`${label} — ${displayName}`}
    >
      <span className="app-avatar" aria-hidden="true">
        {initials(displayName)}
      </span>
      <span className="app-userbtn-text">
        <span className="app-userbtn-name">{displayName}</span>
        <span className="app-userbtn-role">{secondary}</span>
      </span>
    </button>
  );
});
