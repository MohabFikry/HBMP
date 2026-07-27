// Flat ESLint config for the Mersal frontend workspace (apps/*, libs/contracts). Phase 16.8 (H7): a real
// lint gate beyond the existing `tsc --noEmit` type-check. Kept to the recommended, non-type-checked rule
// sets so it runs fast in CI and stays signal-over-noise; the code was already authored with eslint-disable
// directives in mind. Run: `pnpm lint:eslint`.
import js from "@eslint/js";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";

export default tseslint.config(
  { ignores: ["**/dist/**", "**/node_modules/**", "**/coverage/**", "**/*.d.ts", "**/.vite/**"] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: "module",
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: { "react-hooks": reactHooks },
    rules: {
      // React hooks correctness (rules registered explicitly so inline disable directives resolve).
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
      "@typescript-eslint/no-unused-vars": ["error", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
    },
  },
  {
    /**
     * 18.D2 (audit R2 U7) — ban the formatting calls that silently use the wrong zone and locale.
     *
     * `toLocaleDateString()` / `toLocaleTimeString()` / `toLocaleString()` format in the MACHINE's time zone
     * and the BROWSER's locale. Neither is right here: display is Africa/Cairo (CLAUDE.md) and the locale
     * follows the APP's language, which is unrelated to the browser's. The failure is silent — a UTC-set
     * clinic PC renders a 09:00 appointment as 07:00 and nothing errors — so a lint rule is the only thing
     * that catches the next one at authoring time. `useFormat()` is the sanctioned path.
     */
    files: ["apps/web/src/**/*.{ts,tsx}"],
    ignores: ["apps/web/src/i18n/useFormat.ts"],
    rules: {
      "no-restricted-syntax": [
        "error",
        {
          selector:
            "CallExpression > MemberExpression[property.name=/^toLocale(Date|Time)?String$/]",
          message:
            "Use useFormat() — toLocale*String formats in the machine's time zone and the browser's locale; " +
            "display must be Africa/Cairo in the app's language (18.D2 / U7).",
        },
      ],
    },
  },
  {
    // Test + config files: allow the pragmatic patterns fixtures/mocks use.
    files: ["**/test/**/*.{ts,tsx}", "**/*.config.{ts,mts}", "**/vite-env.d.ts"],
    rules: {
      "@typescript-eslint/no-explicit-any": "off",
      "@typescript-eslint/no-non-null-assertion": "off",
    },
  },
);
