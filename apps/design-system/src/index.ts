/**
 * @mersal/design-system — the shared Mersal HBMP visual language (Phase 9.1).
 * Tokens (CSS vars + TS types), Radix-based component library, i18n/RTL, theming, and the brand lockup.
 * Import the stylesheet once at app root: `import "@mersal/design-system/styles.css";`
 */
export * from "./components";
export { useTableQuery } from "./lib/useTableQuery";
export type { TableQuery, TableFilterSpec, UseTableQueryOptions } from "./lib/useTableQuery";
export { compareSortValues, sortRows } from "./lib/sortRows";
export type { SortValue, SortDirection } from "./lib/sortRows";
export * from "./tokens/tokens";
export { ThemeProvider, useTheme } from "./theme/ThemeProvider";
export { useDirection } from "./lib/useDirection";
export { initI18n, en, ar } from "./i18n";
export type { Dictionary } from "./i18n";
