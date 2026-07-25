/** English (LTR) design-system + gallery strings. App portals extend these with their own namespaces. */
export const en = {
  ds: {
    galleryTitle: "Mersal HBMP — Design System",
    gallerySub: "Tokens, components, i18n/RTL and theming — the shared visual language for every portal.",
    theme: "Theme",
    language: "Language",
    light: "Light",
    dark: "Dark",
    // section headers
    sec_logo: "Brand & logo lockup",
    sec_buttons: "Buttons",
    sec_status: "Status chips (color-blind safe)",
    sec_fields: "Inputs & fields",
    sec_seg: "Segmented control",
    sec_tabs: "Tabs",
    sec_table: "Data table / worklist",
    sec_kpi: "KPI cards",
    sec_nav: "Navigation rail",
    sec_modal: "Modal & toast",
    // controls
    primary: "Primary",
    secondary: "Secondary",
    ghost: "Ghost",
    danger: "Danger",
    loading: "Loading",
    openModal: "Open modal",
    showToast: "Show toast",
    save: "Save",
    cancel: "Cancel",
    search: "Search",
    fieldLabel: "Beneficiary name",
    fieldHelp: "As printed on the member card.",
    fieldError: "This field is required.",
  },
  status: {
    ok: "Approved",
    info: "Under review",
    part: "Partial",
    warn: "Emergency",
    bad: "Rejected",
    neu: "Info requested",
  },
} as const;

/** Widen literal string values to `string` so other locales (ar) can supply their own text. */
type Widen<T> = { [K in keyof T]: T[K] extends string ? string : Widen<T[K]> };
export type Dictionary = Widen<typeof en>;
