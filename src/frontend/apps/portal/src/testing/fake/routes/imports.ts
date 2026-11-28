import type { ImportJob } from "../types";
import { json, problem, type Route } from "../http";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** The Imports API, with the worker's steps run at the next read of the job. */
export function importsRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { imports } = s;
  function importsRoute(method: string, url: URL): Response | null {
    if (!url.pathname.startsWith("/api/v1/imports")) {
      return null;
    }

    const [, id, part] = /^\/api\/v1\/imports(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname) ?? [];
    const report = options.importReport ?? { issues: [], rows: [{ row: 2, key: "CAR-NORD", action: "create" }] };
    const errors = report.issues.filter((i) => i.severity === "error").length;
    const count = (action: string) => report.rows.filter((r) => r.action === action).length;
    if (id === "templates" && method === "GET") {
      return new Response(new Uint8Array([0x50, 0x4b, 0x03, 0x04]), { status: 200, headers: { "Content-Type": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" } });
    }

    if (id === undefined && method === "POST") {
      const job: ImportJob = {
        id: `0192f0c1-0000-7000-8000-00000000900${String(imports.length + 1)}`, kind: url.searchParams.get("kind") ?? "", fileName: url.searchParams.get("fileName") ?? "",
        size: 4, sha256: "b".repeat(64), status: "validating", rows: 0, errors: 0, warnings: 0, created: 0, updated: 0, unchanged: 0, scanner: null,
        uploadedAt: "2026-10-28T08:00:00Z", changedAt: "2026-10-28T08:00:00Z", fileKept: true,
      };
      imports.unshift(job);
      return json(202, job);
    }

    if (id === undefined) {
      return json(200, { items: imports, nextCursor: null });
    }

    const index = imports.findIndex((j) => j.id === id);
    const job = imports[index];
    if (!job) {
      return problem(404, "imports.job_not_found");
    }

    const save = (next: Partial<ImportJob>) => {
      s.importSteps += 1;
      imports[index] = { ...job, ...next, changedAt: `2026-10-28T08:00:${String(s.importSteps).padStart(2, "0")}Z` };
      return json(200, imports[index]);
    };
    switch (true) {
      case part === undefined && job.status === "validating":
        return save({
          status: "validated", rows: report.rows.length + errors, errors,
          warnings: report.issues.length - errors, created: 0, updated: 0, unchanged: 0,
        });
      case part === undefined && job.status === "committing":
        return save({ status: "committed", created: count("create"), updated: count("update"), unchanged: count("unchanged") });
      case part === undefined:
        return json(200, job);
      case part === "issues":
        return json(200, { items: report.issues, nextCursor: null });
      case part === "rows":
        return json(200, { items: errors > 0 ? [] : report.rows, nextCursor: null });
      case part === "commit":
        return errors > 0 ? problem(422, "imports.job_has_errors") : save({ status: "committing" });
      case part === "discard":
        return save({ status: "discarded" });
      default:
        return problem(404, "test.unknown_route");
    }
  }

  return [(ctx) => importsRoute(ctx.method, ctx.url)];
}
