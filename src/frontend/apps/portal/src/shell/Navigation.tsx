import { Link } from "@tanstack/react-router";
import { Home, MapPinned, Settings, ShieldCheck, Truck, type LucideIcon } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { cn } from "@karry/ui";
import { sections, type SectionKey } from "./sections";

const icons: Record<SectionKey, LucideIcon> = {
  "portal.nav.home": Home,
  "portal.nav.sites": MapPinned,
  "portal.nav.assets": Truck,
  "portal.nav.audit": ShieldCheck,
  "portal.nav.settings": Settings,
};

const groups = [
  { key: "portal.nav.group.operations", sections: sections.filter((s) => s.group === "operations") },
  { key: "portal.nav.group.administration", sections: sections.filter((s) => s.group === "administration") },
] as const;

/** A label hidden while the sidebar is reduced, shown when it opens over the page. */
export const fadeWhenCollapsed = "opacity-0 transition-opacity duration-200 group-hover/side:opacity-100 group-has-[:focus-visible]/side:opacity-100";

/** The portal's sections in two groups (operations, administration); the current one is marked for screen readers too. */
export function Navigation({ collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?: () => void }) {
  const { t } = useTranslation();
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
