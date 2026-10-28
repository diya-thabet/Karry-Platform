import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { registerSW } from "virtual:pwa-register";
import { createI18n, resolveLanguage } from "@karry/i18n";
import { initErrorTracking } from "@karry/observability";
import { openOfflineDb } from "@karry/offline";
import { savedLanguage } from "@karry/ui";
import { App } from "./App";
import { browserScanner } from "./machines";
import { pullChanges } from "./sync";
import { codeOf, companyOf, deviceInfo, enrollTablet } from "./tablet";
import { unlock } from "./unlock";
import { createUpdates } from "./updates";
import "./index.css";

const release = (import.meta.env.VITE_RELEASE as string | undefined) ?? "dev";
initErrorTracking({
  dsn: import.meta.env.VITE_ERROR_TRACKING_DSN as string | undefined,
  release,
  environment: import.meta.env.MODE,
  app: "field",
});

const language = resolveLanguage({ user: savedLanguage(), browser: navigator.languages });
document.documentElement.lang = language;
const i18n = await createI18n({ language, pseudo: import.meta.env.VITE_PSEUDO_LOCALE === "1" });

// No sign-in: the tablet is an enrolled device that signs its requests, operators unlock it with their PIN (ADR-0028).
const db = openOfflineDb();
const code = codeOf(location);
if (code !== null) {
  // The code is a secret: out of the address bar and the history once read.
  history.replaceState(null, "", "/");
}

const updates = createUpdates();
const updateServiceWorker = registerSW({ onNeedRefresh: () => { updates.offer(() => updateServiceWorker(true)); } });

const root = document.getElementById("root");
if (!root) {
  throw new Error("Missing #root element");
}

createRoot(root).render(
  <StrictMode>
    <App i18n={i18n} environment={{
      db,
      updates,
      persisted: "persist" in navigator.storage ? navigator.storage.persist() : Promise.resolve(false),
      company: companyOf(location.hostname),
      code,
      enroll: async (input) => enrollTablet(db, input, await deviceInfo(release)),
      pull: () => pullChanges(db),
      unlock: (employeeId, pin) => unlock(db, employeeId, pin),
      scanner: await browserScanner(),
    }} />
  </StrictMode>,
);
