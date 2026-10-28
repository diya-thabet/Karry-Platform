import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowRight, FileSpreadsheet, Layers, MapPinned, Plus, Truck, UserPlus, Users } from "lucide-react";
import { useAuth } from "@karry/auth";
import { useTranslation } from "@karry/i18n";
import { buttonVariants, Card, EmptyState, PageHeader, StatCard } from "@karry/ui";
import { useAccess, useApi, useCurrentTenant } from "./api";
import { AssetsManagePermission, AssetsReadPermission } from "./assets/api";
import { ImportsPermission } from "./imports/api";
import { MaterialsReadPermission, useMaterials } from "./materials/api";
import { SitesManagePermission, SitesReadPermission, useSites } from "./sites/api";

const countLimit = 200;

/**
 * The home page: what the company has in Karry (sites, machines, materials, people, each only for those who may see
 * it), the next things to do, and the sites. Counts stop at 200 ("200+"): the home page never pages through a list.
 */
export function HomePage() {
  const { t } = useTranslation();
  const api = useApi();
  const { user } = useAuth();
  const { tenant } = useCurrentTenant();
  const { loaded, can } = useAccess();
  const sites = useSites();
  const materials = useMaterials();
  const machines = useQuery({
    queryKey: ["assets", "home-count"],
    queryFn: async () => (await api.GET("/api/v1/assets", { params: { query: { limit: countLimit } } })).data,
    enabled: can(AssetsReadPermission),
  });
  const people = useQuery({
    queryKey: ["identity", "users", "home-count"],
    queryFn: async () => (await api.GET("/api/v1/identity/users", { params: { query: { limit: countLimit } } })).data,
    enabled: can("identity.users.read"),
  });

  const count = (items: unknown[] | undefined, more = false) => (items === undefined ? "…" : more ? `${String(countLimit)}+` : String(items.length));
  const activeSites = sites.data?.filter((s) => s.status === "active");
  const stats = [
    can(SitesReadPermission) && { key: "sites", icon: MapPinned, label: t("portal.home.sites"), value: count(activeSites) },
    can(AssetsReadPermission) && { key: "machines", icon: Truck, label: t("portal.home.machines"), value: count(machines.data?.items, Boolean(machines.data?.nextCursor)) },
    can(MaterialsReadPermission) && { key: "materials", icon: Layers, label: t("portal.home.materials"), value: count(materials.data) },
    can("identity.users.read") && {
      key: "people", icon: Users, label: t("portal.home.people"),
      value: count(people.data?.items.filter((u) => u.status !== "deactivated"), Boolean(people.data?.nextCursor)),
    },
  ].filter((stat) => stat !== false);

  const actions = [
    can(ImportsPermission) && { to: "/settings/imports", icon: FileSpreadsheet, label: t("portal.home.action.import"), hint: t("portal.home.action.importHint") },
    can(SitesManagePermission) && { to: "/sites/new", icon: Plus, label: t("portal.home.action.site"), hint: t("portal.home.action.siteHint") },
    can(AssetsManagePermission) && { to: "/assets/new", icon: Truck, label: t("portal.home.action.machine"), hint: t("portal.home.action.machineHint") },
    can("identity.users.manage") && { to: "/settings/users", icon: UserPlus, label: t("portal.home.action.people"), hint: t("portal.home.action.peopleHint") },
  ].filter((action) => action !== false);

  const firstName = user?.name.split(" ")[0] ?? "";
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={firstName ? t("portal.home.hello", { name: firstName }) : t("portal.nav.home")}
        description={tenant ? t("portal.home.overview", { company: tenant.name }) : undefined}
      />

      {loaded && stats.length === 0 && actions.length === 0 && <EmptyState title={t("portal.home.welcome", { company: tenant?.name ?? t("common.appName") })} />}

      {stats.length > 0 && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {stats.map((stat) => <StatCard key={stat.key} icon={stat.icon} label={stat.label} value={stat.value} />)}
        </div>
      )}

      <div className="grid gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
        {actions.length > 0 && (
          <Card title={t("portal.home.actions")} titleId="home-actions">
            <ul className="flex flex-col gap-1">
              {actions.map((action) => {
                const Icon = action.icon;
                return (
                  <li key={action.to}>
                    <Link to={action.to} className="group flex items-center gap-3 rounded-lg p-2 hover:bg-surface-muted">
                      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary-soft text-primary">
                        <Icon aria-hidden className="size-4" />
                      </span>
                      <span className="flex min-w-0 flex-1 flex-col">
                        <span className="text-sm font-medium text-text">{action.label}</span>
                        <span className="text-xs text-text-muted">{action.hint}</span>
                      </span>
                      <ArrowRight aria-hidden className="size-4 text-text-muted group-hover:text-primary" />
                    </Link>
                  </li>
                );
              })}
            </ul>
          </Card>
        )}

        {can(SitesReadPermission) && (
          <Card
            title={t("portal.home.yourSites")}
            titleId="home-sites"
            actions={<Link to="/sites" className={buttonVariants({ variant: "ghost" })}>{t("portal.home.allSites")}</Link>}
          >
            {activeSites && activeSites.length > 0
              ? (
                <ul className="flex flex-col divide-y divide-divider">
                  {activeSites.slice(0, 6).map((site) => (
                    <li key={site.id}>
                      <Link to="/sites/$siteId" params={{ siteId: site.id }} className="flex items-center justify-between gap-3 py-3 hover:text-primary">
                        <span className="flex min-w-0 flex-col">
                          <span className="truncate text-sm font-medium" translate="no">{site.name}</span>
                          <span className="text-xs text-text-muted">{`${site.code} · ${t(`portal.sites.type.${site.type}`)} · ${site.address.city}`}</span>
                        </span>
                        <ArrowRight aria-hidden className="size-4 shrink-0 text-text-muted" />
                      </Link>
                    </li>
                  ))}
                </ul>
              )
              : <p className="text-sm text-text-muted">{sites.isPending ? t("common.loading") : t("portal.home.noSites")}</p>}
          </Card>
        )}
      </div>
    </div>
  );
}
