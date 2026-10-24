import { Link, Outlet } from "@tanstack/react-router";
import { useAuth } from "@karry/auth";
import { useTranslation } from "@karry/i18n";
import { Button, EmptyState, LanguageSwitcher, ThemeSwitcher } from "@karry/ui";
import { sections, type SectionKey } from "./sections";

/** Portal layout: header with switchers, main navigation (permission- and plan-driven from P1.9/P1.10), content. */
export function Layout() {
  const { t } = useTranslation();
  return (
    <div className="min-h-screen bg-background text-text">
      <header className="flex flex-wrap items-end justify-between gap-4 border-b-[length:var(--karry-border-width)] border-border bg-surface px-6 py-4">
        <div>
          <p className="text-2xl font-bold">{t("portal.title")}</p>
          <p className="text-text-muted">{t("portal.subtitle")}</p>
        </div>
        <div className="flex flex-wrap items-end gap-4">
          <LanguageSwitcher />
          <ThemeSwitcher />
          <Account />
        </div>
      </header>
      <div className="flex flex-col md:flex-row">
        <nav aria-label={t("portal.nav.label")} className="border-b-[length:var(--karry-border-width)] border-border bg-surface p-4 md:min-h-[calc(100vh-6rem)] md:w-56 md:border-b-0 md:border-r-[length:var(--karry-border-width)]">
          <ul className="flex flex-wrap gap-2 md:flex-col">
            {sections.map((section) => (
              <li key={section.path}>
                <Link
                  to={section.path}
                  className="flex min-h-[var(--karry-control-height)] items-center rounded-md px-3 font-semibold text-text hover:bg-background"
                  activeProps={{ "aria-current": "page", className: "bg-primary text-primary-text hover:bg-primary" }}
                  activeOptions={{ exact: section.path === "/" }}
                >
                  {t(section.key)}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <main className="flex-1 p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

/** Who is signed in, and sign-out (US-P1-IAM-01). */
function Account() {
  const { t } = useTranslation();
  const { user, signOut } = useAuth();
  return (
    <div className="flex items-center gap-3">
      <span className="text-text-muted">{t("common.account.signedInAs", { name: user?.name ?? "" })}</span>
      <Button variant="secondary" onClick={() => void signOut()}>
        {t("common.account.signOut")}
      </Button>
    </div>
  );
}

export function Section({ titleKey }: { titleKey: SectionKey }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-2xl font-bold">{t(titleKey)}</h1>
      <EmptyState title={t("common.empty.title")} />
    </div>
  );
}

export function NotFound() {
  const { t } = useTranslation();
  return <EmptyState title={t("portal.notFound")} />;
}

