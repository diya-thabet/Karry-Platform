import { Link, Outlet } from "@tanstack/react-router";
import { ChevronDown, Globe, Home, LogOut, MapPinned, Palette, Settings, ShieldCheck, Truck, type LucideIcon } from "lucide-react";
import { useAuth } from "@karry/auth";
import { supportedLanguages, useTranslation } from "@karry/i18n";
import { chooseLanguage, EmptyState, Menu, PageHeader, themeNames, useTheme, type ThemeName } from "@karry/ui";
import { SubscriptionGate, SubscriptionNotice } from "./billing/SubscriptionNotice";
import { UsageBanner } from "./billing/UsageBanner";
import { CompanyBrand, CompanyLanguage, CompanyStripe } from "./CompanyBrand";
import { sections, type SectionKey } from "./sections";

const icons: Record<SectionKey, LucideIcon> = {
  "portal.nav.home": Home,
  "portal.nav.sites": MapPinned,
  "portal.nav.assets": Truck,
  "portal.nav.audit": ShieldCheck,
  "portal.nav.settings": Settings,
};

/**
 * Portal layout: a sidebar with the company and the navigation in two groups (operations, administration), a slim
 * top bar with the person's menu (language, theme, sign-out), and the page. On narrow screens the sidebar becomes a
 * bar above the page.
 */
export function Layout() {
  const { t } = useTranslation();
  const groups = [
    { key: "portal.nav.group.operations", sections: sections.filter((s) => s.group === "operations") },
    { key: "portal.nav.group.administration", sections: sections.filter((s) => s.group === "administration") },
  ];
  return (
    <div className="min-h-screen bg-background text-text">
      <CompanyLanguage />
      <header className="sticky top-0 z-30 border-b-[length:var(--karry-border-width)] border-divider bg-surface">
        <CompanyStripe />
        <div className="flex h-16 items-center justify-between gap-3 px-4 md:px-5">
          <CompanyBrand />
          <AccountMenu />
        </div>
      </header>
      <div className="md:flex">
        <aside className="border-b-[length:var(--karry-border-width)] border-divider bg-surface md:sticky md:top-16 md:h-[calc(100vh-4rem)] md:w-64 md:shrink-0 md:border-b-0 md:border-r-[length:var(--karry-border-width)]">
          <nav aria-label={t("portal.nav.label")} className="h-full overflow-y-auto px-3 py-2 md:py-4">
            {groups.map((group) => (
              <div key={group.key} className="flex flex-col gap-1 md:mb-5">
                <p className="hidden px-3 pb-1 text-xs font-medium uppercase tracking-wide text-text-muted md:block">{t(group.key)}</p>
                <ul className="flex flex-wrap gap-1 md:flex-col">
                  {group.sections.map((section) => {
                    const Icon = icons[section.key];
                    return (
                      <li key={section.path}>
                        <Link
                          to={section.path}
                          className="flex min-h-10 items-center gap-3 rounded-lg px-3 text-sm font-medium text-text-muted hover:bg-surface-muted hover:text-text"
                          activeProps={{ "aria-current": "page", className: "bg-primary-soft !text-primary hover:bg-primary-soft" }}
                          activeOptions={{ exact: section.path === "/" }}
                        >
                          <Icon aria-hidden className="size-4.5 shrink-0" />
                          {t(section.key)}
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))}
          </nav>
        </aside>
        <div className="flex min-w-0 flex-1 flex-col">
          <SubscriptionNotice />
          <UsageBanner />
          <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 md:px-8 md:py-8">
            <SubscriptionGate>
              <Outlet />
            </SubscriptionGate>
          </main>
        </div>
      </div>
    </div>
  );
}

function initials(name: string): string {
  const letters = name.split(/\s+/).filter(Boolean).map((part) => part[0]?.toUpperCase() ?? "");
  return (letters.length > 1 ? `${letters[0] ?? ""}${letters.at(-1) ?? ""}` : (letters[0] ?? "?"));
}

/** Who is signed in (US-P1-IAM-01), with the language, the theme and sign-out in one menu. */
function AccountMenu() {
  const { t, i18n } = useTranslation();
  const { user, signOut } = useAuth();
  const { theme, setTheme } = useTheme();
  const name = user?.name ?? "";
  return (
    <Menu
      label={t("common.account.signedInAs", { name })}
      trigger={(
        <button type="button" className="flex min-h-10 items-center gap-2.5 rounded-lg px-2 text-sm hover:bg-surface-muted">
          <span aria-hidden className="flex size-8 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-text">{initials(name)}</span>
          <span className="hidden font-medium text-text sm:inline" translate="no">{name}</span>
          <ChevronDown aria-hidden className="size-4 text-text-muted" />
        </button>
      )}
      groups={[
        { label: t("common.account.signedInAs", { name }), items: [] },
        {
          label: t("common.language"),
          items: supportedLanguages.map((language) => ({
            key: `language-${language}`, label: t(`common.language.${language}`), icon: <Globe aria-hidden className="size-4 text-text-muted" />,
            checked: i18n.language === language, onSelect: () => { chooseLanguage(i18n, language); },
          })),
        },
        {
          label: t("common.theme"),
          items: themeNames.map((name: ThemeName) => ({
            key: `theme-${name}`, label: t(`common.theme.${name}`), icon: <Palette aria-hidden className="size-4 text-text-muted" />,
            checked: theme === name, onSelect: () => { setTheme(name); },
          })),
        },
        { items: [{ key: "sign-out", label: t("common.account.signOut"), icon: <LogOut aria-hidden className="size-4 text-text-muted" />, onSelect: () => void signOut() }] },
      ]}
    />
  );
}

export function Section({ titleKey }: { titleKey: SectionKey }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-6">
      <PageHeader title={t(titleKey)} />
      <EmptyState title={t("portal.comingSoon")}>{t("portal.comingSoonHint")}</EmptyState>
    </div>
  );
}

export function NotFound() {
  const { t } = useTranslation();
  return <EmptyState title={t("portal.notFound")} action={<Link to="/" className="text-sm font-medium text-primary hover:underline">{t("portal.backHome")}</Link>} />;
}
