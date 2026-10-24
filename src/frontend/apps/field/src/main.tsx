import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createAuth } from "@karry/auth";
import { createI18n, resolveLanguage } from "@karry/i18n";
import { initErrorTracking } from "@karry/observability";
import { savedLanguage } from "@karry/ui";
import { App } from "./App";
import "./index.css";

initErrorTracking({
  dsn: import.meta.env.VITE_ERROR_TRACKING_DSN as string | undefined,
  release: (import.meta.env.VITE_RELEASE as string | undefined) ?? "dev",
  environment: import.meta.env.MODE,
  app: "field",
});

const language = resolveLanguage({ user: savedLanguage(), browser: navigator.languages });
document.documentElement.lang = language;
const i18n = await createI18n({ language, pseudo: import.meta.env.VITE_PSEUDO_LOCALE === "1" });

// Sign-in before anything renders (US-P1-IAM-01): back from Keycloak, or on the way there. Tokens stay in memory.
const auth = createAuth({
  authority: (import.meta.env.VITE_AUTH_AUTHORITY as string | undefined) ?? "http://localhost:8080/realms/karry",
  clientId: "karry-field",
  language: () => i18n.language,
});
await auth.start();

const root = document.getElementById("root");
if (!root) {
  throw new Error("Missing #root element");
}

createRoot(root).render(
  <StrictMode>
    <App i18n={i18n} auth={auth} />
  </StrictMode>,
);
