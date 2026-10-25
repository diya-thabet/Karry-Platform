import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router";
import { Layout, NotFound, Section } from "./Layout";
import { sections } from "./sections";
import { RoleEditorPage, RolesPage } from "./settings/RolesPages";
import { SettingsPage } from "./settings/SettingsPage";
import { UsersPage } from "./settings/UsersPage";

const root = createRootRoute({ component: Layout, notFoundComponent: NotFound });
const routes = sections
  .filter((section) => section.path !== "/settings")
  .map((section) => createRoute({ getParentRoute: () => root, path: section.path, component: () => <Section titleKey={section.key} /> }));

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

export function createPortalRouter(history?: RouterHistory) {
  return createRouter({ routeTree: root.addChildren([...routes, settings, roles, newRole, role, users]), ...(history ? { history } : {}) });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createPortalRouter>;
  }
}
