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
//
// sessionStorage matters as much as localStorage now that screens restore their state from it
// (`useRestorableState`): jsdom keeps one store for the whole file, so without this an open call left behind
// by one test would be restored into the next one's freshly-rendered workspace — and the leak would look like
// a component bug rather than a fixture bug.
afterEach(() => {
  localStorage.clear();
  sessionStorage.clear();
});
