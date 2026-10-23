import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createI18n, resolveLanguage } from "@karry/i18n";
import { initErrorTracking } from "@karry/observability";
import { savedLanguage } from "@karry/ui";
import { App } from "./App";
import "./index.css";

initErrorTracking({
  dsn: import.meta.env.VITE_ERROR_TRACKING_DSN as string | undefined,
  release: (import.meta.env.VITE_RELEASE as string | undefined) ?? "dev",
  environment: import.meta.env.MODE,
  app: "portal",
});

// Company default language arrives with tenant settings (P1.11); until then: user choice, browser, French.
const language = resolveLanguage({ user: savedLanguage(), browser: navigator.languages });
document.documentElement.lang = language;
const i18n = await createI18n({ language, pseudo: import.meta.env.VITE_PSEUDO_LOCALE === "1" });

const root = document.getElementById("root");
if (!root) {
  throw new Error("Missing #root element");
}

createRoot(root).render(
  <StrictMode>
    <App i18n={i18n} />
  </StrictMode>,
);
