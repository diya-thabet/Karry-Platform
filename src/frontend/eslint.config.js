// Shared lint rules for every frontend package (ADR-0020): type-aware typescript-eslint rules.
import js from "@eslint/js";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  // Generated code is checked by its generator's freshness check and by tsc, not by style rules.
  { ignores: ["**/dist/**", "**/coverage/**", "**/node_modules/**", "**/storybook-static/**", "**/.vitest/**", "packages/api-client/src/schema.ts"] },
  js.configs.recommended,
  ...tseslint.configs.strictTypeChecked,
  ...tseslint.configs.stylisticTypeChecked,
  {
    languageOptions: {
      globals: { ...globals.browser },
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      // Offline sync code must never drop a promise silently.
      "@typescript-eslint/no-floating-promises": "error",
      "@typescript-eslint/consistent-type-imports": "error",
    },
  },
  {
    // Every package with React code, not only the apps: the design system's components use hooks too (UX-22).
    files: ["apps/**/*.{ts,tsx}", "packages/*/src/**/*.{ts,tsx}"],
    plugins: { "react-hooks": reactHooks },
    rules: reactHooks.configs.recommended.rules,
  },
  {
    // Fast refresh needs files that export components only; the packages export helpers beside them on purpose.
    files: ["apps/**/*.{ts,tsx}"],
    plugins: { "react-refresh": reactRefresh },
    rules: { "react-refresh/only-export-components": ["error", { allowConstantExport: true }] },
  },
  {
    // The apps' service worker scripts (Web Push, ADR-0039 §5): plain JavaScript run in the worker, not type-checked.
    files: ["apps/*/public/**/*.js"],
    ...tseslint.configs.disableTypeChecked,
    languageOptions: { globals: { ...globals.serviceworker }, parserOptions: { projectService: false, project: null } },
  },
  {
    // Build scripts of the packages (e.g. the k6 bundle of @karry/crypto): plain Node modules, not type-checked.
    files: ["packages/*/scripts/**/*.mjs"],
    ...tseslint.configs.disableTypeChecked,
    languageOptions: { globals: { ...globals.node }, parserOptions: { projectService: false, project: null } },
  },
  {
    files: ["*.js", "**/*.config.{js,ts}"],
    ...tseslint.configs.disableTypeChecked,
    languageOptions: { globals: { ...globals.node } },
  },
);
