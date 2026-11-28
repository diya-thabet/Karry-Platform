import type { IntegrityFlag } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import type { FakeState } from "../state";

/** Integrity flags (settling a gap included) and the company's rules. */
export function integrityRoutes(s: FakeState): Route[] {
  const { integrityRules, settlements, integrityFlags } = s;
  const settlement: Route = ({ request, url, body }) => {
    const settleMatch = /^\/api\/v1\/integrity\/flags\/([^/]+)\/(settle|settlement\/approve|settlement\/decline)$/.exec(url.pathname);
    if (request.method === "POST" && settleMatch) {
      // Settling a continuity gap (ADR-0037 §7): on the flag's version (If-Match), else 409.
      const index = integrityFlags.findIndex((f) => f.id === settleMatch[1]);
      const flag = integrityFlags[index];
      if (!flag) {
        return problem(404, "integrity.flag_not_found");
      }

      settlements.push({ action: settleMatch[2] ?? "", ifMatch: request.headers.get("If-Match"), body });
      if (request.headers.get("If-Match") !== (flag.version ?? "")) {
        return problem(409, "request.version_conflict");
      }

      const input = body as { reason?: string; comment?: string } | null;
      const next: IntegrityFlag = settleMatch[2] === "settle"
        ? { ...flag, settlement: { reason: input?.reason ?? "", comment: input?.comment ?? "", proposedBy: "test-user", proposedAt: "2026-11-25T08:00:00Z" } }
        : settleMatch[2] === "settlement/approve"
          ? { ...flag, closedAt: "2026-11-25T09:00:00Z", closeReason: `settled:${flag.settlement?.reason ?? ""}` }
          : { ...flag, settlement: null };
      integrityFlags[index] = { ...next, version: nextVersion(flag.version ?? '"1"') };
      return json(200, integrityFlags[index]);
    }
    return null;
  };

  const flagsAndRules: Route = ({ request, url, body }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/integrity/flags") {
      // Open by default, closed or all; by site and rule; a page of `limit` after the `cursor` flag (ADR-0036 §2).
      const status = url.searchParams.get("status") ?? "open";
      const site = url.searchParams.get("siteId");
      const code = url.searchParams.get("rule");
      const limit = Number(url.searchParams.get("limit") ?? "50");
      const matching = integrityFlags.filter((f) => (status === "all" || (status === "open") === (f.closedAt === null))
        && (!site || f.siteId === site) && (!code || f.rule === code));
      const cursor = url.searchParams.get("cursor");
      const from = cursor ? matching.findIndex((f) => f.id === cursor) + 1 : 0;
      const page = matching.slice(from, from + limit);
      return json(200, { items: page, nextCursor: from + limit < matching.length ? (page.at(-1)?.id ?? null) : null });
    }

    const ruleMatch = /^\/api\/v1\/integrity\/rules(?:\/([^/]+))?$/.exec(url.pathname);
    if (ruleMatch) {
      const code = ruleMatch[1];
      if (request.method === "GET" && code === undefined) {
        return json(200, { items: integrityRules });
      }

      const index = integrityRules.findIndex((r) => r.rule === code);
      const found = integrityRules[index];
      if (request.method !== "PUT" || !found) {
        return problem(404, "integrity.rule_not_found");
      }

      const ifMatch = request.headers.get("If-Match");
      if (ifMatch !== null && ifMatch !== found.version) {
        return problem(409, "request.version_conflict");
      }

      const input = body as { enabled: boolean; thresholds: Record<string, string> | null };
      if (found.security && !input.enabled) {
        return problem(400, "integrity.rule_locked");
      }

      integrityRules[index] = {
        ...found,
        enabled: input.enabled,
        thresholds: found.thresholds.map((t) => ({ ...t, value: input.thresholds?.[t.name] ?? t.value })),
        changedAt: "2026-11-19T10:00:00Z",
        changedBy: "admin-1",
        version: nextVersion(found.version),
      };
      return json(200, integrityRules[index]);
    }
    return null;
  };

  return [settlement, flagsAndRules];
}
