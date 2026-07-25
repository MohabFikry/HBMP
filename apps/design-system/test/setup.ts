import "@testing-library/jest-dom/vitest";
import { expect } from "vitest";
import { toHaveNoViolations } from "jest-axe";
import { initI18n } from "../src/i18n";

// Register the axe matcher for the accessibility gate.
expect.extend(toHaveNoViolations);

// Ensure i18next is initialized once for all component tests.
initI18n("en");

// jsdom lacks matchMedia; provide a no-op so ThemeProvider can read prefers-color-scheme.
if (!window.matchMedia) {
  window.matchMedia = ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}
