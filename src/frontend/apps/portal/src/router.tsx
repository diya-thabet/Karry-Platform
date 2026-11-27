import { createRootRouteWithContext, createRoute, createRouter, redirect, type RouterHistory } from "@tanstack/react-router";
import { AssetPage, AssetsPage } from "./assets/AssetsPages";
import { AuditPage } from "./audit/AuditPage";
import { ChecklistPage, ChecklistsPage } from "./checklists/ChecklistsPages";
import { HoldsPage } from "./holds/HoldsPage";
import { PlanPage } from "./billing/PlanPage";
import { HomePage } from "./home/HomePage";
import { ImportsPage } from "./imports/ImportsPage";
import { ComingSoon } from "./shell/Placeholders";
import { MaterialPage, MaterialsPage } from "./materials/MaterialsPages";
import { TenantPage } from "./operator/TenantPage";
import { TenantsPage } from "./operator/TenantsPage";
import { sections } from "./shell/sections";
import { RoleEditorPage, RolesPage } from "./settings/RolesPages";
import { Shell, ShellNotFound } from "./shell/Shell";
import { CompanyPage } from "./settings/CompanyPage";
import { FlagsPage } from "./integrity/FlagsPage";
import { RulesPage } from "./integrity/RulesPage";
import { PoliciesPage } from "./verification/PoliciesPage";
import { NotificationsPage } from "./notifications/NotificationsPage";
import { NoticeRulesPage } from "./notifications/RulesPage";
import { BoardPage } from "./verification/BoardPage";
import { EntryHistoryPage } from "./verification/EntryHistory";
import { QueuePage } from "./verification/QueuePage";
import { SettingsPage } from "./settings/SettingsPage";
import { SupportPage } from "./settings/SupportPage";
import { TabletsPage } from "./settings/TabletsPage";
import { UsersPage } from "./settings/UsersPage";
import { SitePage, SitesPage } from "./sites/SitesPages";

/** Who the router serves: a platform operator without a company gets the operator console (ADR-0029 §5). */
export interface PortalContext {
  operator: boolean;
  /** The company of the operator's last support visit: back in the console, its page opens instead of the list. */
  lastCompany?: string | undefined;
}

const root = createRootRouteWithContext<PortalContext>()({ component: Shell, notFoundComponent: ShellNotFound });

// A company's screens: a platform operator is sent to the console instead.
const company = createRoute({
  getParentRoute: () => root,
  id: "company",
  beforeLoad: ({ context }) => {
    if (context.operator && context.lastCompany) {
      redirect({ to: "/operator/companies/$tenantId", params: { tenantId: context.lastCompany }, throw: true });
    }

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
const operatorCompany = createRoute({
  getParentRoute: () => operatorArea,
  path: "/operator/companies/$tenantId",
  component: function OperatorCompany() {
    const { tenantId } = operatorCompany.useParams();
    return <TenantPage tenantId={tenantId} />;
  },
});
const routes = sections
  .filter((section) => !["/", "/settings", "/sites", "/assets", "/audit", "/review"].includes(section.path))
  .map((section) => createRoute({ getParentRoute: () => company, path: section.path, component: () => <ComingSoon titleKey={section.key} /> }));

const home = createRoute({ getParentRoute: () => company, path: "/", component: HomePage });
const audit = createRoute({ getParentRoute: () => company, path: "/audit", component: AuditPage });
const review = createRoute({ getParentRoute: () => company, path: "/review", component: QueuePage });
const board = createRoute({ getParentRoute: () => company, path: "/review/board", component: BoardPage });
const entryHistory = createRoute({ getParentRoute: () => company, path: "/review/history", component: EntryHistoryPage });
const notifications = createRoute({ getParentRoute: () => company, path: "/notifications", component: NotificationsPage });
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
const support = createRoute({ getParentRoute: () => company, path: "/settings/support", component: SupportPage });
const companySettings = createRoute({ getParentRoute: () => company, path: "/settings/company", component: CompanyPage });
const integrityRules = createRoute({ getParentRoute: () => company, path: "/settings/integrity", component: RulesPage });
const reviewPolicies = createRoute({ getParentRoute: () => company, path: "/settings/review-policies", component: PoliciesPage });
const noticeRules = createRoute({ getParentRoute: () => company, path: "/settings/notifications", component: NoticeRulesPage });
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
const checklists = createRoute({ getParentRoute: () => company, path: "/settings/checklists", component: ChecklistsPage });
const newChecklist = createRoute({ getParentRoute: () => company, path: "/settings/checklists/new", component: () => <ChecklistPage /> });
const checklist = createRoute({
  getParentRoute: () => company,
  path: "/settings/checklists/$assetType",
  component: function Checklist() {
    const { assetType } = checklist.useParams();
    return <ChecklistPage key={assetType} assetType={assetType} />;
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
const holds = createRoute({ getParentRoute: () => company, path: "/assets/holds", component: HoldsPage });
const integrityFlags = createRoute({ getParentRoute: () => company, path: "/assets/integrity", component: FlagsPage });
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
        home, ...routes, audit, review, board, entryHistory, notifications, settings, roles, newRole, role, users, plan, imports, tablets, support, companySettings, integrityRules, reviewPolicies, noticeRules, materials, newMaterial, material, checklists, newChecklist, checklist, sites, newSite, site, assets, holds, integrityFlags, newAsset, asset,
      ]),
      operatorArea.addChildren([companies, operatorCompany]),
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
