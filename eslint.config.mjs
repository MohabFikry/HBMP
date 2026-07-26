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
    // Test + config files: allow the pragmatic patterns fixtures/mocks use.
    files: ["**/test/**/*.{ts,tsx}", "**/*.config.{ts,mts}", "**/vite-env.d.ts"],
    rules: {
      "@typescript-eslint/no-explicit-any": "off",
      "@typescript-eslint/no-non-null-assertion": "off",
    },
  },
);
