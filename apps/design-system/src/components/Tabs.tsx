import * as RadixTabs from "@radix-ui/react-tabs";
import type { ReactNode } from "react";
import { cx } from "../lib/cx";
import { useDirection } from "../lib/useDirection";

export interface TabItem {
  value: string;
  label: string;
  content: ReactNode;
}

export interface TabsProps {
  items: TabItem[];
  value: string;
  onValueChange: (value: string) => void;
  "aria-label": string;
  className?: string;
  /**
   * Visual style only — semantics (tablist/tab/tabpanel roles, roving focus, arrow-key nav) are identical
   * either way. "underline" (default, 0B §6) suits a document-style set of panes. "pill" gives the same
   * segmented-control look `SegmentedControl` uses, for a tab bar that reads as primary page navigation
   * rather than a filter — `SegmentedControl` itself stays a `radiogroup`, the correct role for an actual
   * filter switch, so a content-switching tab bar should not borrow it just for the look.
   */
  variant?: "underline" | "pill";
}

/**
 * Tabs — Radix-backed (roving focus, arrow-key nav, correct ARIA). Underline style per 0B §6, or pill.
 * Content is always mounted so SSR/loading never hides a panel unexpectedly.
 */
export function Tabs({ items, value, onValueChange, className, variant = "underline", ...aria }: TabsProps) {
  /*
   * Radix owns the roving focus, so it is Radix that has to be told which way the row runs. Left to itself it
   * assumes `ltr` — there is no `DirectionProvider` in this app — and in Arabic every tab bar arrowed
   * backwards. Passed as a prop rather than by adding `@radix-ui/react-direction`: `Root` already accepts it,
   * and one prop is a smaller thing to keep true than a provider somebody has to remember to mount.
   */
  const dir = useDirection();
  return (
    <RadixTabs.Root value={value} onValueChange={onValueChange} className={className} dir={dir}>
      <RadixTabs.List
        className={cx("mrs-tabs", variant === "pill" && "mrs-tabs--pill")}
        aria-label={aria["aria-label"]}
      >
        {items.map((it) => (
          <RadixTabs.Trigger key={it.value} value={it.value} className="mrs-tab" asChild>
            <button type="button">{it.label}</button>
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {items.map((it) => (
        <RadixTabs.Content
          key={it.value}
          value={it.value}
          className={cx("mrs-tabpane")}
          forceMount
          hidden={it.value !== value}
        >
          {it.content}
        </RadixTabs.Content>
      ))}
    </RadixTabs.Root>
  );
}
