import { createRootRouteWithContext, createRoute, createRouter, redirect, type RouterHistory } from "@tanstack/react-router";
import { AssetPage, AssetsPage } from "./assets/AssetsPages";
import { AuditPage } from "./audit/AuditPage";
import { PlanPage } from "./billing/PlanPage";
import { HomePage } from "./HomePage";
import { ImportsPage } from "./imports/ImportsPage";
import { Section } from "./Layout";
import { MaterialPage, MaterialsPage } from "./materials/MaterialsPages";
import { TenantsPage } from "./operator/TenantsPage";
import { sections } from "./sections";
import { RoleEditorPage, RolesPage } from "./settings/RolesPages";
import { Shell, ShellNotFound } from "./Shell";
import { CompanyPage } from "./settings/CompanyPage";
import { SettingsPage } from "./settings/SettingsPage";
import { TabletsPage } from "./settings/TabletsPage";
import { UsersPage } from "./settings/UsersPage";
import { SitePage, SitesPage } from "./sites/SitesPages";

/** Who the router serves: a platform operator without a company gets the operator console (ADR-0029 §5). */
export interface PortalContext {
  operator: boolean;
}

const root = createRootRouteWithContext<PortalContext>()({ component: Shell, notFoundComponent: ShellNotFound });

// A company's screens: a platform operator is sent to the console instead.
const company = createRoute({
  getParentRoute: () => root,
  id: "company",
  beforeLoad: ({ context }) => {
    if (context.operator) {
      redirect({ to: "/operator", throw: true });
    }
  },
});

// The operator console: Karry staff on the platform address, never a company's people.
const operatorArea = createRoute({
  getParentRoute: () => root,
  id: "operator",
  beforeLoad: ({ context }) => {
    if (!context.operator) {
      redirect({ to: "/", throw: true });
    }
  },
});
const companies = createRoute({ getParentRoute: () => operatorArea, path: "/operator", component: TenantsPage });
const routes = sections
  .filter((section) => !["/", "/settings", "/sites", "/assets", "/audit"].includes(section.path))
  .map((section) => createRoute({ getParentRoute: () => company, path: section.path, component: () => <Section titleKey={section.key} /> }));

const home = createRoute({ getParentRoute: () => company, path: "/", component: HomePage });
const audit = createRoute({ getParentRoute: () => company, path: "/audit", component: AuditPage });
const settings = createRoute({ getParentRoute: () => company, path: "/settings", component: SettingsPage });
const roles = createRoute({ getParentRoute: () => company, path: "/settings/roles", component: RolesPage });
const newRole = createRoute({ getParentRoute: () => company, path: "/settings/roles/new", component: () => <RoleEditorPage /> });
const role = createRoute({
  getParentRoute: () => company,
  path: "/settings/roles/$code",
  component: function Role() {
    const { code } = role.useParams();
    return <RoleEditorPage code={code} />;
  },
});
const users = createRoute({ getParentRoute: () => company, path: "/settings/users", component: UsersPage });
const plan = createRoute({ getParentRoute: () => company, path: "/settings/plan", component: PlanPage });
const imports = createRoute({ getParentRoute: () => company, path: "/settings/imports", component: ImportsPage });
const tablets = createRoute({ getParentRoute: () => company, path: "/settings/tablets", component: TabletsPage });
const companySettings = createRoute({ getParentRoute: () => company, path: "/settings/company", component: CompanyPage });
const materials = createRoute({ getParentRoute: () => company, path: "/settings/materials", component: MaterialsPage });
const newMaterial = createRoute({ getParentRoute: () => company, path: "/settings/materials/new", component: () => <MaterialPage /> });
const material = createRoute({
  getParentRoute: () => company,
  path: "/settings/materials/$materialId",
  component: function Material() {
    const { materialId } = material.useParams();
    return <MaterialPage key={materialId} materialId={materialId} />;
  },
});
const sites = createRoute({ getParentRoute: () => company, path: "/sites", component: SitesPage });
const newSite = createRoute({ getParentRoute: () => company, path: "/sites/new", component: () => <SitePage /> });
const site = createRoute({
  getParentRoute: () => company,
  path: "/sites/$siteId",
  component: function Site() {
    const { siteId } = site.useParams();
    return <SitePage key={siteId} siteId={siteId} />;
  },
});

const assets = createRoute({ getParentRoute: () => company, path: "/assets", component: AssetsPage });
const newAsset = createRoute({ getParentRoute: () => company, path: "/assets/new", component: () => <AssetPage /> });
const asset = createRoute({
  getParentRoute: () => company,
  path: "/assets/$assetId",
  component: function Asset() {
    const { assetId } = asset.useParams();
    return <AssetPage key={assetId} assetId={assetId} />;
  },
});

export function createPortalRouter(history?: RouterHistory, context: PortalContext = { operator: false }) {
  return createRouter({
    routeTree: root.addChildren([
      company.addChildren([
        home, ...routes, audit, settings, roles, newRole, role, users, plan, imports, tablets, companySettings, materials, newMaterial, material, sites, newSite, site, assets, newAsset, asset,
      ]),
      operatorArea.addChildren([companies]),
    ]),
    context,
    ...(history ? { history } : {}),
  });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createPortalRouter>;
  }
}
