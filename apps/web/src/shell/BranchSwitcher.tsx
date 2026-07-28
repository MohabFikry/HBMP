import { useState } from "react";
import { Icon, Select, useTheme } from "@mersal/design-system";
import type { SelectOption } from "@mersal/design-system";
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
 * MemberScoped roles see an "All branches" indicator plus an OPTIONAL filter — never a restriction.
 *
 * Built on the design-system Select rather than a native <select>: the OS draws a native option list itself,
 * so it came up system-blue and square-cornered against a teal app bar and no CSS could reach it. Select keeps
 * the same keyboard contract (arrows, Home/End, typeahead, Escape) with the list under our own tokens.
 */
export function BranchSwitcher({ memberScoped, branches, activeBranchId, onSwitch, onFilter }: BranchSwitcherProps) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang as "en" | "ar"];
  const [announce, setAnnounce] = useState("");

  // "Home" is a qualifier on the name, not part of it — it goes in `hint` so it renders muted instead of
  // competing with the branch name at the same weight.
  const toOption = (b: BranchOption): SelectOption => ({
    value: b.id,
    label: b.name,
    hint: b.isHome ? t(L.homeBranch) : undefined,
  });

  if (memberScoped) {
    return (
      <div className="branch-switcher branch-switcher--member">
        {onFilter && branches.length > 0 ? (
          <Select
            className="branch-select"
            shape="pill"
            aria-label={t(L.branch)}
            leadingIcon={<Icon name="branch" aria-hidden />}
            placeholder={t(L.allBranches)}
            value={activeBranchId}
            options={[{ value: "", label: t(L.allBranches) }, ...branches.map(toOption)]}
            onChange={(v) => onFilter(v || null)}
          />
        ) : (
          <>
            <Icon name="branch" className="branch-glyph" aria-hidden />
            <span data-testid="all-branches-indicator">{t(L.allBranches)}</span>
          </>
        )}
      </div>
    );
  }

  return (
    <div className="branch-switcher">
      {/* The icon sits inside the control and carries the meaning; the accessible name is on the combobox, so
          "Active branch" no longer spends a third of the app bar restating what the control already shows. */}
      <Select
        className="branch-select"
        shape="pill"
        aria-label={t(L.activeBranch)}
        leadingIcon={<Icon name="branch" aria-hidden />}
        value={activeBranchId}
        options={branches.map(toOption)}
        onChange={(id) => {
          const picked = branches.find((b) => b.id === id);
          onSwitch(id);
          if (picked) setAnnounce(`${t(L.branchSwitched)} ${picked.name}`);
        }}
      />
      {/* Non-visual: announce the change to screen readers (design 37 §7 / a11y DoD). */}
      <span aria-live="polite" className="sr-only" data-testid="branch-live">{announce}</span>
    </div>
  );
}
