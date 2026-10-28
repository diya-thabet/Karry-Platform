import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { useState } from "react";
import { ChevronDown, Globe, Home, LogOut, MapPinned, Menu as MenuIcon, Palette, PanelLeftClose, PanelLeftOpen, Settings, ShieldCheck, Truck, type LucideIcon } from "lucide-react";
import { useAuth } from "@karry/auth";
import { supportedLanguages, useTranslation } from "@karry/i18n";
import { chooseLanguage, cn, Drawer, EmptyState, Menu, PageHeader, themeNames, useTheme, type ThemeName } from "@karry/ui";
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

const sidebarKey = "karry.sidebar";

function rememberedCollapsed(): boolean {
  try {
    return localStorage.getItem(sidebarKey) === "collapsed";
  } catch {
    return false;
  }
}

/**
 * Portal layout: a top bar (company, the person's menu), a sidebar navigation in two groups (operations,
 * administration) and the page. On a computer the sidebar can be reduced to its icons (remembered on this browser); a
 * reduced sidebar opens over the page while the mouse is on it or the keyboard is in it. On a phone the navigation is a
 * drawer behind the menu button.
 */
export function Layout() {
  const { t } = useTranslation();
  const [collapsed, setCollapsed] = useState(rememberedCollapsed);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const toggle = () => {
    const next = !collapsed;
    setCollapsed(next);
    try {
      localStorage.setItem(sidebarKey, next ? "collapsed" : "expanded");
    } catch {
      // Private browsing: the choice lasts until the page is closed.
    }
  };

  return (
    <div className="min-h-screen bg-background text-text">
      <CompanyLanguage />
      <header className="sticky top-0 z-30 border-b-[length:var(--karry-border-width)] border-divider bg-surface/95 backdrop-blur supports-[backdrop-filter]:bg-surface/85">
        <CompanyStripe />
        <div className="flex h-16 items-center justify-between gap-2 px-3 md:px-5">
          <div className="flex min-w-0 items-center gap-1">
            <button
              type="button"
              aria-label={t("portal.nav.open")}
              aria-expanded={drawerOpen}
              onClick={() => { setDrawerOpen(true); }}
              className="flex size-10 shrink-0 items-center justify-center rounded-lg text-text-muted transition-colors hover:bg-surface-muted hover:text-text md:hidden"
            >
              <MenuIcon aria-hidden className="size-5" />
            </button>
            <CompanyBrand />
          </div>
          <AccountMenu />
        </div>
      </header>
      <Drawer open={drawerOpen} onOpenChange={setDrawerOpen} title={t("portal.nav.label")} closeLabel={t("common.close")} header={<CompanyBrand />}>
        <Navigation onNavigate={() => { setDrawerOpen(false); }} />
      </Drawer>
      <div className="flex">
        <aside
          data-collapsed={collapsed}
          className={cn("relative z-20 hidden shrink-0 transition-[width] duration-200 ease-out md:block", collapsed ? "w-[4.5rem]" : "w-64")}
        >
          <div
            className={cn(
              "group/side sticky top-16 flex h-[calc(100vh-4rem)] flex-col overflow-x-hidden border-r-[length:var(--karry-border-width)] border-divider bg-surface transition-[width,box-shadow] duration-200 ease-out",
              collapsed ? "w-[4.5rem] hover:w-64 hover:shadow-xl has-[:focus-visible]:w-64 has-[:focus-visible]:shadow-xl" : "w-64",
            )}
          >
            <Navigation collapsed={collapsed} />
            <div className="border-t-[length:var(--karry-border-width)] border-divider p-3">
              <button
                type="button"
                onClick={toggle}
                className="flex min-h-10 w-full items-center gap-3 rounded-lg px-3 text-sm font-medium text-text-muted transition-colors hover:bg-surface-muted hover:text-text"
              >
                {collapsed ? <PanelLeftOpen aria-hidden className="size-4.5 shrink-0" /> : <PanelLeftClose aria-hidden className="size-4.5 shrink-0" />}
                <span className={cn("whitespace-nowrap", collapsed && fadeWhenCollapsed)}>{t(collapsed ? "portal.nav.pin" : "portal.nav.collapse")}</span>
              </button>
            </div>
          </div>
        </aside>
        <div className="flex min-w-0 flex-1 flex-col">
          <SubscriptionNotice />
          <UsageBanner />
          <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-5 sm:py-6 md:px-8 md:py-8">
            <SubscriptionGate>
              <Page />
            </SubscriptionGate>
          </main>
        </div>
      </div>
    </div>
  );
}

/** A label hidden while the sidebar is reduced, shown when it opens over the page. */
const fadeWhenCollapsed = "opacity-0 transition-opacity duration-200 group-hover/side:opacity-100 group-has-[:focus-visible]/side:opacity-100";

/** The page, faded in when the person moves to another screen (not when a list's detail changes). */
function Page() {
  const screen = useRouterState({ select: (state) => state.matches.at(-1)?.routeId ?? "" });
  return <div key={screen} className="karry-page-enter"><Outlet /></div>;
}

function Navigation({ collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?: () => void }) {
  const { t } = useTranslation();
  const groups = [
    { key: "portal.nav.group.operations", sections: sections.filter((s) => s.group === "operations") },
    { key: "portal.nav.group.administration", sections: sections.filter((s) => s.group === "administration") },
  ];
  return (
    <nav aria-label={t("portal.nav.label")} className="flex-1 overflow-y-auto overflow-x-hidden px-3 py-4">
      {groups.map((group) => (
        <div key={group.key} className="mb-5 flex flex-col gap-1">
          <p className={cn("whitespace-nowrap px-3 pb-1 text-xs font-medium uppercase tracking-wide text-text-muted", collapsed && fadeWhenCollapsed)}>{t(group.key)}</p>
          <ul className="flex flex-col gap-1">
            {group.sections.map((section) => {
              const Icon = icons[section.key];
              return (
                <li key={section.path}>
                  <Link
                    to={section.path}
                    onClick={onNavigate}
                    className="flex min-h-10 items-center gap-3 rounded-lg px-3 text-sm font-medium text-text-muted transition-colors hover:bg-surface-muted hover:text-text"
                    activeProps={{ "aria-current": "page", className: "bg-primary-soft !text-primary hover:bg-primary-soft" }}
                    activeOptions={{ exact: section.path === "/" }}
                  >
                    <Icon aria-hidden className="size-4.5 shrink-0" />
                    <span className={cn("whitespace-nowrap", collapsed && fadeWhenCollapsed)}>{t(section.key)}</span>
                  </Link>
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </nav>
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
        <button type="button" className="flex min-h-10 items-center gap-2.5 rounded-lg px-2 text-sm transition-colors hover:bg-surface-muted data-[state=open]:bg-surface-muted">
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
