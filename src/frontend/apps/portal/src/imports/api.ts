import type { components } from "@karry/api-client";

export type ImportJob = components["schemas"]["ImportResponse"];
export type ImportIssue = components["schemas"]["ImportIssueResponse"];
export type ImportPlannedRow = components["schemas"]["ImportRowResponse"];

export const ImportsPermission = "imports.files.manage";

export const workbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

/**
 * The kinds of data a file can load, in the order a new company loads them (machines and users point to sites, meters
 * to machines), each with the permission that lets the person change that data: the API checks both (ADR-0027 §5).
 */
export const importKinds = [
  { kind: "sites", permission: "organization.sites.manage" },
  { kind: "materials", permission: "materials.catalog.manage" },
  { kind: "assets", permission: "assets.registry.manage" },
  { kind: "meters", permission: "assets.registry.manage" },
  { kind: "users", permission: "identity.users.manage" },
] as const;

/** At most this many issues and preview rows are shown (the API's page size); a file with more is fixed in parts. */
export const shownLimit = 200;

export const importsKey = ["imports"] as const;
export const importKey = (id: string) => ["imports", id] as const;

/** A job the worker is still on: its page polls until it is validated, committed or discarded. */
export function isRunning(job: ImportJob | undefined): boolean {
  return job?.status === "validating" || job?.status === "committing";
}

/** Saves a file the API sent (a template) through a temporary link; does nothing where the browser cannot (tests). */
export function saveFile(blob: Blob, name: string): void {
  if (typeof URL.createObjectURL !== "function") {
    return;
  }

  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
}
