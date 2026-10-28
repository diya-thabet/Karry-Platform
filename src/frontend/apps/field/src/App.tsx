import { useCallback, useEffect, useState } from "react";
import { Download, Droplets, Gauge, HardDriveDownload, Lock, Mountain, TriangleAlert, type LucideIcon } from "lucide-react";
import { I18nProvider, useTranslation, type I18n } from "@karry/i18n";
import type { DeviceRecord, KarryOfflineDb, SettingsRecord } from "@karry/offline";
import { Button, LanguageSwitcher, ThemeProvider, ThemeSwitcher } from "@karry/ui";
import { Enroll } from "./Enroll";
import { ReferenceStatus } from "./ReferenceStatus";
import type { PullOutcome } from "./sync";
import type { EnrollFailure, EnrollInput } from "./tablet";
import { useAutoLock } from "./autoLock";
import { Unlock } from "./UnlockScreen";
import type { Session, UnlockFailure } from "./unlock";
import { useUpdateAvailable, type AppUpdates } from "./updates";

const actions: { key: "field.action.fuel" | "field.action.meter" | "field.action.production" | "field.action.issue"; icon: LucideIcon }[] = [
  { key: "field.action.fuel", icon: Droplets },
  { key: "field.action.meter", icon: Gauge },
  { key: "field.action.production", icon: Mountain },
  { key: "field.action.issue", icon: TriangleAlert },
];

/** Where the app runs and what it can do there; built by main.tsx, replaced by fakes in tests. */
export interface FieldEnvironment {
  db: KarryOfflineDb;
  updates: AppUpdates;
  /** Whether the browser keeps the tablet's data under storage pressure (US-P1-OFF-01 AC3); null while unknown. */
  persisted: Promise<boolean>;
  /** The company named by the address, null on a bare local address. */
  company: string | null;
  /** The enrollment code of the opened link, if any. */
  code: string | null;
  enroll: (input: EnrollInput) => Promise<DeviceRecord | EnrollFailure>;
  /** Refreshes the reference data (a delta, or a snapshot when one is due). */
  pull: () => Promise<PullOutcome>;
  /** Checks a field user's employee ID and PIN, online when it can, else on the tablet. */
  unlock: (employeeId: string, pin: string) => Promise<Session | UnlockFailure>;
}

/** A new version, offered only here on the home screen, never during a form (US-P1-OFF-01 AC2). */
function UpdateOffer({ updates }: { updates: AppUpdates }) {
  const { t } = useTranslation();
  const [applying, setApplying] = useState(false);
  if (!useUpdateAvailable(updates)) {
    return null;
  }

  return (
    <section aria-label={t("field.update.label")} className="flex flex-wrap items-center gap-3 rounded-xl border-[length:var(--karry-border-width)] border-primary bg-surface p-4">
      <Download aria-hidden className="size-8 text-primary" />
      <p className="flex-1 font-medium">{t("field.update.ready")}</p>
      <Button disabled={applying} onClick={() => { setApplying(true); void updates.apply(); }}>{t("field.update.install")}</Button>
    </section>
  );
}

function StorageWarning({ persisted }: { persisted: Promise<boolean> }) {
  const { t } = useTranslation();
  const [kept, setKept] = useState(true);
  useEffect(() => { void persisted.then(setKept); }, [persisted]);
  if (kept) {
    return null;
  }

  return (
    <section role="alert" className="flex items-start gap-3 rounded-xl border-[length:var(--karry-border-width)] border-warning bg-warning-soft p-4">
      <HardDriveDownload aria-hidden className="size-8 shrink-0 text-warning" />
      <div>
        <h2 className="font-semibold">{t("field.storage.title")}</h2>
        <p>{t("field.storage.body")}</p>
      </div>
    </section>
  );
}

/**
 * Field home (US-P1-OFF-01 shell): who has the tablet and a button to lock it, large actions for gloves, the state of
 * the tablet's offline data. It locks itself after the server's idle time (US-P1-OFF-04 AC3).
 */
function Home({ environment, device, session, autoLockMinutes, lock }: {
  environment: FieldEnvironment; device: DeviceRecord; session: Session; autoLockMinutes: number; lock: () => void;
}) {
  const { t } = useTranslation();
  useAutoLock(autoLockMinutes, lock);
  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-6 p-4">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex flex-col gap-1">
          <h1 className="text-3xl font-bold">{t("field.title")}</h1>
          <p className="text-text-muted">{t("field.subtitle")}</p>
        </div>
        <div className="flex items-center gap-3">
          <p className="font-semibold">{t("field.unlock.user", { name: session.name, employeeId: session.employeeId })}</p>
          <Button variant="secondary" onClick={lock}>
            <Lock aria-hidden className="size-5" />
            {t("field.unlock.lock")}
          </Button>
        </div>
      </header>
      <UpdateOffer updates={environment.updates} />
      <StorageWarning persisted={environment.persisted} />
      <ReferenceStatus db={environment.db} device={device} pull={environment.pull} />
      <main>
        <nav aria-label={t("field.nav.label")}>
          <ul className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {actions.map(({ key, icon: Icon }) => (
              <li key={key}>
                <Button block variant="secondary" className="min-h-28 flex-col text-xl">
                  <Icon aria-hidden className="size-10" />
                  {t(key)}
                </Button>
              </li>
            ))}
          </ul>
        </nav>
      </main>
      <footer className="mt-auto grid gap-4 sm:grid-cols-2">
        <LanguageSwitcher />
        <ThemeSwitcher />
      </footer>
    </div>
  );
}

/** Enrolment until this tablet has a device key, then the lock screen until someone unlocks it, then the home screen. */
function Tablet({ environment }: { environment: FieldEnvironment }) {
  const [device, setDevice] = useState<DeviceRecord | null | undefined>(undefined);
  const [settings, setSettings] = useState<SettingsRecord | undefined>(undefined);
  // Who has the tablet: in memory only, so closing the app locks it too.
  const [session, setSession] = useState<Session | null>(null);
  const lock = useCallback(() => { setSession(null); }, []);
  useEffect(() => {
    void environment.db.device.get("this").then((found) => { setDevice(found ?? null); });
    void environment.db.settings.get("this").then(setSettings);
  }, [environment.db]);

  if (device === undefined) {
    return null;
  }

  if (device === null) {
    return (
      <Enroll company={environment.company} code={environment.code} enroll={async (input) => {
        const result = await environment.enroll(input);
        if (typeof result !== "string") {
          setSettings(await environment.db.settings.get("this"));
          setDevice(result);
        }

        return result;
      }} />
    );
  }

  if (session === null) {
    return <Unlock unlock={environment.unlock} onUnlocked={setSession} />;
  }

  return <Home environment={environment} device={device} session={session} autoLockMinutes={settings?.autoLockMinutes ?? 5} lock={lock} />;
}

/** Field app: outdoor high-contrast theme by default, field density (≥ 48 px targets, ≥ 16 px text). */
export function App({ i18n, environment }: { i18n: I18n; environment: FieldEnvironment }) {
  return (
    <I18nProvider i18n={i18n}>
      <ThemeProvider defaultTheme="outdoor" density="field">
        <Tablet environment={environment} />
      </ThemeProvider>
    </I18nProvider>
  );
}
