/** Portal sections in their sidebar groups (navigation driven by permissions and plan from P1.9/P1.10). */
export const sections = [
  { path: "/", key: "portal.nav.home", group: "operations" },
  { path: "/sites", key: "portal.nav.sites", group: "operations" },
  { path: "/assets", key: "portal.nav.assets", group: "operations" },
  { path: "/audit", key: "portal.nav.audit", group: "administration" },
  { path: "/settings", key: "portal.nav.settings", group: "administration" },
] as const;

export type SectionKey = (typeof sections)[number]["key"];
