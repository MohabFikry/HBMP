import { Fragment, type ReactNode } from "react";
import { cx } from "../lib/cx";

export interface NavItem {
  key: string;
  label: string;
  icon?: ReactNode;
  /** Group heading this item sits under. */
  group?: string;
  /** Real destination URL. When present the item renders as an anchor, so middle-click/ctrl-click open a
   *  new tab and assistive tech announces navigation — a nav made of buttons has neither (QA P2-17). */
  href?: string;
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
      {/* Keyed by POSITION, never by the group's display name. Two groups can legitimately share a name
          (the caller controls ordering), and the name is localized — so name-keys collide within a render
          and all change on a language switch, which left React reconciling against stale fragments: the
          previous language's groups stayed in the DOM, hoisted above the new ones (QA P1-8/P1-9). */}
      {groups.map((g, gi) => (
        <Fragment key={gi}>
          {g.name && <div className="mrs-grp">{g.name}</div>}
          {g.items.map((it) =>
            it.href ? (
              <a
                key={it.key}
                href={it.href}
                className="mrs-navi"
                aria-current={it.key === current ? "page" : undefined}
                onClick={(e) => {
                  // Plain left-click stays an SPA navigation; modified clicks and middle-click keep their
                  // native meaning (new tab / new window), which is the whole reason this is an anchor.
                  if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
                  e.preventDefault();
                  onNavigate(it.key);
                }}
              >
                {it.icon}
                <span>{it.label}</span>
              </a>
            ) : (
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
            ),
          )}
        </Fragment>
      ))}
    </nav>
  );
}
