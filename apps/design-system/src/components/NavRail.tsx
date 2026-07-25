import { Fragment, type ReactNode } from "react";
import { cx } from "../lib/cx";

export interface NavItem {
  key: string;
  label: string;
  icon?: ReactNode;
  /** Group heading this item sits under. */
  group?: string;
}

export interface NavRailProps {
  items: NavItem[];
  current: string;
  onNavigate: (key: string) => void;
  "aria-label": string;
  className?: string;
}

/**
 * Navigation rail — level-2 glass, permission-generated items grouped with hairline dividers + micro-labels,
 * current item marked with accent bar + aria-current="page". Collapses to a bottom tab bar on mobile via CSS.
 * The caller passes ONLY the items the user is allowed to see (min-necessary menus, 0B §6 / 14 §2).
 */
export function NavRail({ items, current, onNavigate, className, ...aria }: NavRailProps) {
  // Preserve order while grouping.
  const groups: Array<{ name?: string; items: NavItem[] }> = [];
  for (const it of items) {
    const last = groups[groups.length - 1];
    if (last && last.name === it.group) last.items.push(it);
    else groups.push({ name: it.group, items: [it] });
  }

  return (
    <nav className={cx("mrs-rail", className)} aria-label={aria["aria-label"]}>
      {groups.map((g, gi) => (
        <Fragment key={g.name ?? `g${gi}`}>
          {g.name && <div className="mrs-grp">{g.name}</div>}
          {g.items.map((it) => (
            <button
              key={it.key}
              type="button"
              className="mrs-navi"
              aria-current={it.key === current ? "page" : undefined}
              onClick={() => onNavigate(it.key)}
            >
              {it.icon}
              <span>{it.label}</span>
            </button>
          ))}
        </Fragment>
      ))}
    </nav>
  );
}
