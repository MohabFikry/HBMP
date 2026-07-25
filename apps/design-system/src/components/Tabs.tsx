import * as RadixTabs from "@radix-ui/react-tabs";
import type { ReactNode } from "react";
import { cx } from "../lib/cx";

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
}

/**
 * Tabs — Radix-backed (roving focus, arrow-key nav, correct ARIA). Underline style per 0B §6.
 * Content is always mounted so SSR/loading never hides a panel unexpectedly.
 */
export function Tabs({ items, value, onValueChange, className, ...aria }: TabsProps) {
  return (
    <RadixTabs.Root value={value} onValueChange={onValueChange} className={className}>
      <RadixTabs.List className="mrs-tabs" aria-label={aria["aria-label"]}>
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
