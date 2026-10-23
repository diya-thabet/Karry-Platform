/** Portal sections (navigation driven by permissions and plan from P1.9/P1.10). */
export const sections = [
  { path: "/", key: "portal.nav.home" },
  { path: "/sites", key: "portal.nav.sites" },
  { path: "/assets", key: "portal.nav.assets" },
  { path: "/audit", key: "portal.nav.audit" },
  { path: "/settings", key: "portal.nav.settings" },
] as const;

export type SectionKey = (typeof sections)[number]["key"];
