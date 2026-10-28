import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router";
import { AssetPage, AssetsPage } from "./assets/AssetsPages";
import { PlanPage } from "./billing/PlanPage";
import { HomePage } from "./HomePage";
import { ImportsPage } from "./imports/ImportsPage";
import { Layout, NotFound, Section } from "./Layout";
import { MaterialPage, MaterialsPage } from "./materials/MaterialsPages";
import { sections } from "./sections";
import { RoleEditorPage, RolesPage } from "./settings/RolesPages";
import { CompanyPage } from "./settings/CompanyPage";
import { SettingsPage } from "./settings/SettingsPage";
import { UsersPage } from "./settings/UsersPage";
import { SitePage, SitesPage } from "./sites/SitesPages";

const root = createRootRoute({ component: Layout, notFoundComponent: NotFound });
const routes = sections
  .filter((section) => !["/", "/settings", "/sites", "/assets"].includes(section.path))
  .map((section) => createRoute({ getParentRoute: () => root, path: section.path, component: () => <Section titleKey={section.key} /> }));

const home = createRoute({ getParentRoute: () => root, path: "/", component: HomePage });
const settings = createRoute({ getParentRoute: () => root, path: "/settings", component: SettingsPage });
const roles = createRoute({ getParentRoute: () => root, path: "/settings/roles", component: RolesPage });
const newRole = createRoute({ getParentRoute: () => root, path: "/settings/roles/new", component: () => <RoleEditorPage /> });
const role = createRoute({
  getParentRoute: () => root,
  path: "/settings/roles/$code",
  component: function Role() {
    const { code } = role.useParams();
    return <RoleEditorPage code={code} />;
  },
});
const users = createRoute({ getParentRoute: () => root, path: "/settings/users", component: UsersPage });
const plan = createRoute({ getParentRoute: () => root, path: "/settings/plan", component: PlanPage });
const imports = createRoute({ getParentRoute: () => root, path: "/settings/imports", component: ImportsPage });
const company = createRoute({ getParentRoute: () => root, path: "/settings/company", component: CompanyPage });
const materials = createRoute({ getParentRoute: () => root, path: "/settings/materials", component: MaterialsPage });
const newMaterial = createRoute({ getParentRoute: () => root, path: "/settings/materials/new", component: () => <MaterialPage /> });
const material = createRoute({
  getParentRoute: () => root,
  path: "/settings/materials/$materialId",
  component: function Material() {
    const { materialId } = material.useParams();
    return <MaterialPage key={materialId} materialId={materialId} />;
  },
});
const sites = createRoute({ getParentRoute: () => root, path: "/sites", component: SitesPage });
const newSite = createRoute({ getParentRoute: () => root, path: "/sites/new", component: () => <SitePage /> });
const site = createRoute({
  getParentRoute: () => root,
  path: "/sites/$siteId",
  component: function Site() {
    const { siteId } = site.useParams();
    return <SitePage key={siteId} siteId={siteId} />;
  },
});

const assets = createRoute({ getParentRoute: () => root, path: "/assets", component: AssetsPage });
const newAsset = createRoute({ getParentRoute: () => root, path: "/assets/new", component: () => <AssetPage /> });
const asset = createRoute({
  getParentRoute: () => root,
  path: "/assets/$assetId",
  component: function Asset() {
    const { assetId } = asset.useParams();
    return <AssetPage key={assetId} assetId={assetId} />;
  },
});

export function createPortalRouter(history?: RouterHistory) {
  return createRouter({
    routeTree: root.addChildren([
      home, ...routes, settings, roles, newRole, role, users, plan, imports, company, materials, newMaterial, material, sites, newSite, site, assets, newAsset, asset,
    ]),
    ...(history ? { history } : {}),
  });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createPortalRouter>;
  }
}
