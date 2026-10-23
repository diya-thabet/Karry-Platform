import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router";
import { Layout, NotFound, Section } from "./Layout";
import { sections } from "./sections";

const root = createRootRoute({ component: Layout, notFoundComponent: NotFound });
const routes = sections.map((section) =>
  createRoute({ getParentRoute: () => root, path: section.path, component: () => <Section titleKey={section.key} /> }),
);

export function createPortalRouter(history?: RouterHistory) {
  return createRouter({ routeTree: root.addChildren(routes), ...(history ? { history } : {}) });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createPortalRouter>;
  }
}
