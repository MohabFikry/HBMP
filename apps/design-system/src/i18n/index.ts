import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { en } from "./en";
import { ar } from "./ar";

/**
 * Shared i18next instance. Both `en` and `ar` bundles are authored (no runtime machine translation).
 * `dir` switching is handled by ThemeProvider, which mirrors the document via logical CSS properties.
 */
export function initI18n(lng: "en" | "ar" = "en") {
  if (!i18n.isInitialized) {
    void i18n.use(initReactI18next).init({
      resources: {
        en: { translation: en },
        ar: { translation: ar },
      },
      lng,
      fallbackLng: "en",
      interpolation: { escapeValue: false },
      returnNull: false,
    });
  }
  return i18n;
}

export { en, ar };
export type { Dictionary } from "./en";
export default i18n;
