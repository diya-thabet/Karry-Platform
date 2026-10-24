import { Droplets, Gauge, Mountain, TriangleAlert, type LucideIcon } from "lucide-react";
import { AuthProvider, useAuth, type Auth } from "@karry/auth";
import { I18nProvider, useTranslation, type I18n } from "@karry/i18n";
import { Button, LanguageSwitcher, StatusChip, ThemeProvider, ThemeSwitcher } from "@karry/ui";

const actions: { key: "field.action.fuel" | "field.action.meter" | "field.action.production" | "field.action.issue"; icon: LucideIcon }[] = [
  { key: "field.action.fuel", icon: Droplets },
  { key: "field.action.meter", icon: Gauge },
  { key: "field.action.production", icon: Mountain },
  { key: "field.action.issue", icon: TriangleAlert },
];

/** Who is signed in, and sign-out (US-P1-IAM-01); the shared-tablet PIN unlock comes in P1.15. */
function Account() {
  const { t } = useTranslation();
  const { user, signOut } = useAuth();
  return (
    <div className="flex flex-col gap-2 sm:col-span-2">
      <p className="text-text-muted">{t("common.account.signedInAs", { name: user?.name ?? "" })}</p>
      <Button block variant="secondary" onClick={() => void signOut()}>
        {t("common.account.signOut")}
      </Button>
    </div>
  );
}

/** Field home (US-P1-OFF-01 shell): large actions for gloves, sync status (real counts with the outbox, P1.15). */
function Home() {
  const { t } = useTranslation();
  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-6 p-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-3xl font-bold">{t("field.title")}</h1>
        <p className="text-text-muted">{t("field.subtitle")}</p>
      </header>
      <section aria-label={t("field.sync.label")} className="flex items-center gap-3">
        <StatusChip status="pending">{t("field.sync.unknown")}</StatusChip>
      </section>
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
        <Account />
      </footer>
    </div>
  );
}

/** Field app: outdoor high-contrast theme by default, field density (≥ 48 px targets, ≥ 16 px text). */
export function App({ i18n, auth }: { i18n: I18n; auth: Auth }) {
  return (
    <AuthProvider auth={auth}>
      <I18nProvider i18n={i18n}>
        <ThemeProvider defaultTheme="outdoor" density="field">
          <Home />
        </ThemeProvider>
      </I18nProvider>
    </AuthProvider>
  );
}
