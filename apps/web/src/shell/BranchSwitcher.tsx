import { useId, useState } from "react";
import { Icon, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";

/** A permitted branch for the switcher. `isHome` marks the user's home branch (design 37 §2.3). */
export interface BranchOption {
  id: string;
  name: string;
  isHome: boolean;
}

export interface BranchSwitcherProps {
  /** True for member-scoped roles (approvals/director/finance/…): branch is a convenience, never a restriction. */
  memberScoped: boolean;
  /** The permitted set (Home ∪ Additional). Empty for member-scoped callers. */
  branches: BranchOption[];
  /** The currently active branch id (null ⇒ all branches, for member-scoped). */
  activeBranchId: string | null;
  /** Called when a BranchScoped user picks another branch → POST /me/active-branch. */
  onSwitch: (branchId: string) => void;
  /** Optional convenience filter for member-scoped users (null ⇒ all). */
  onFilter?: (branchId: string | null) => void;
}

/**
 * Phase 14.8 — the app-bar branch context control (design 37 §7). BranchScoped roles get a switcher over their
 * permitted branches (Home marked); selecting one changes the active branch and announces it via aria-live.
 * MemberScoped roles see an "All branches" indicator plus an OPTIONAL filter — never a restriction. Native
 * <select> keeps it keyboard-operable, ≥44px, focus-ringed and RTL-mirrored with no custom ARIA to get wrong.
 */
export function BranchSwitcher({ memberScoped, branches, activeBranchId, onSwitch, onFilter }: BranchSwitcherProps) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang as "en" | "ar"];
  const selectId = useId();
  const [announce, setAnnounce] = useState("");

  const label = (b: BranchOption) => (b.isHome ? `${b.name} · ${t(L.homeBranch)}` : b.name);

  if (memberScoped) {
    return (
      <div className="branch-switcher branch-switcher--member">
        <Icon name="branch" aria-hidden />
        <span data-testid="all-branches-indicator">{t(L.allBranches)}</span>
        {onFilter && branches.length > 0 && (
          <>
            <label htmlFor={selectId} className="sr-only">{t(L.branch)}</label>
            <select
              className="branch-select"
              id={selectId}
              value={activeBranchId ?? ""}
              onChange={(e) => onFilter(e.target.value || null)}
            >
              <option value="">{t(L.allBranches)}</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>{label(b)}</option>
              ))}
            </select>
          </>
        )}
      </div>
    );
  }

  return (
    <div className="branch-switcher">
      {/* The icon carries the meaning and the accessible name lives on the select — "Active branch" as a
          visible label spent a third of the app bar restating what the control already shows. */}
      <Icon name="branch" aria-hidden />
      <label htmlFor={selectId} className="sr-only">{t(L.activeBranch)}</label>
      <select
        className="branch-select"
        id={selectId}
        value={activeBranchId ?? ""}
        onChange={(e) => {
          const id = e.target.value;
          const picked = branches.find((b) => b.id === id);
          onSwitch(id);
          if (picked) setAnnounce(`${t(L.branchSwitched)} ${picked.name}`);
        }}
      >
        {branches.map((b) => (
          <option key={b.id} value={b.id}>{label(b)}</option>
        ))}
      </select>
      {/* Non-visual: announce the change to screen readers (design 37 §7 / a11y DoD). */}
      <span aria-live="polite" className="sr-only" data-testid="branch-live">{announce}</span>
    </div>
  );
}
