import "@testing-library/jest-dom/vitest";
import { expect, afterEach } from "vitest";
import { toHaveNoViolations } from "jest-axe";

expect.extend(toHaveNoViolations);

// jsdom lacks matchMedia; ThemeProvider reads prefers-color-scheme.
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

// Each test starts from a clean session store.
afterEach(() => {
  localStorage.clear();
});
