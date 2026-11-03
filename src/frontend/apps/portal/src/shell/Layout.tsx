import { Outlet, useRouterState } from "@tanstack/react-router";
import { useState } from "react";
import { Menu as MenuIcon, PanelLeftClose, PanelLeftOpen } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { cn, Drawer, rememberChoice, rememberedChoice } from "@karry/ui";
import { SubscriptionGate, SubscriptionNotice } from "../billing/SubscriptionNotice";
import { UsageBanner } from "../billing/UsageBanner";
import { AccountMenu } from "./AccountMenu";
import { CompanyBrand, CompanyLanguage, CompanyStripe } from "./CompanyBrand";
import { fadeWhenCollapsed, Navigation } from "./Navigation";

const sidebarKey = "karry.sidebar";
const isSidebarState = (value: string | null): value is "collapsed" | "expanded" => value === "collapsed" || value === "expanded";

/**
 * Portal layout: a top bar (company, the person's menu), a sidebar navigation in two groups (operations,
 * administration) and the page. On a computer the sidebar can be reduced to its icons (remembered on this browser); a
 * reduced sidebar opens over the page while the mouse is on it or the keyboard is in it. On a phone the navigation is a
 * drawer behind the menu button.
 */
export function Layout() {
  const { t } = useTranslation();
  const [collapsed, setCollapsed] = useState(() => rememberedChoice(sidebarKey, isSidebarState) === "collapsed");
  const [drawerOpen, setDrawerOpen] = useState(false);
  const toggle = () => {
    const next = !collapsed;
    setCollapsed(next);
    rememberChoice(sidebarKey, next ? "collapsed" : "expanded");
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

/** The page, faded in when the person moves to another screen (not when a list's detail changes). */
function Page() {
  const screen = useRouterState({ select: (state) => state.matches.at(-1)?.routeId ?? "" });
  return <div key={screen} className="karry-page-enter"><Outlet /></div>;
}
