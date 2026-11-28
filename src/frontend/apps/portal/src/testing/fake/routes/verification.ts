import type { PolicyVersion, ReviewItem } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { reviewItem } from "../factories";
import type { FakeState } from "../state";

/** The review queue, its decisions and the review policy (ADR-0037, ADR-0038). */
export function verificationRoutes(s: FakeState): Route[] {
  const { reviewItems, queueCalls, decisions, correctionWaits, policyVersions } = s;
  const policies: Route = ({ request, url, body }) => {
    if (url.pathname === "/api/v1/verification/policies" && request.method === "GET") {
      // Shift entries' policy: the newest published version, or the defaults (ADR-0037 §5).
      const defaults: PolicyVersion = { version: 0, effectiveFrom: null, autoApprove: false, fourEyesHours: 2, fourEyesShare: 0.05, slaHours: 24, escalateTo: "site-manager", publishedAt: null, publishedBy: null };
      return json(200, { items: [{ recordType: "fieldops.shift_entry", current: policyVersions.at(-1) ?? defaults, versions: policyVersions }] });
    }

    if (url.pathname === "/api/v1/verification/policies/fieldops.shift_entry" && request.method === "PUT") {
      const input = body as Omit<PolicyVersion, "version" | "publishedAt" | "publishedBy">;
      if (Number(input.slaHours) < 1 || Number(input.slaHours) > 168) {
        return problem(400, "verification.policy_invalid");
      }

      const published: PolicyVersion = {
        ...input, version: policyVersions.length + 1, effectiveFrom: "2026-11-24T10:00:00Z", publishedAt: "2026-11-24T10:00:00Z", publishedBy: "admin-1",
      };
      policyVersions.push(published);
      return json(200, published);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/verification/items") {
      // The queue (ADR-0038 §1, §3): pending by default; by site, flag, overdue; by risk (overdue, flagged, oldest);
      // an ETag of the page and a 304 when the caller already has it.
      queueCalls.push({ query: Object.fromEntries(url.searchParams), ifNoneMatch: request.headers.get("If-None-Match") });
      const site = url.searchParams.get("siteId");
      const flag = url.searchParams.get("flag");
      const overdue = url.searchParams.get("overdue") === "true";
      const risk = (item: ReviewItem) => (item.escalatedAt ? 2 : item.flags.length > 0 ? 1 : 0);
      const matching = reviewItems
        .filter((item) => (url.searchParams.get("status") === "all" || item.status === "pending" || item.status === "awaiting_second") && (!site || item.siteId === site)
          && (!flag || item.flags.includes(flag)) && (!overdue || item.escalatedAt !== null))
        .map((item, index) => ({ item, index }))
        .sort((a, b) => (url.searchParams.get("sort") === "risk" ? risk(b.item) - risk(a.item) : 0) || a.index - b.index)
        .map(({ item }) => item)
        .slice(0, Number(url.searchParams.get("limit") ?? "50"));
      const tag = `"${String(matching.map((item) => `${item.id}.${item.version}`).join(";").length)}-${matching.map((item) => item.id.slice(-4)).join("")}"`;
      if (request.headers.get("If-None-Match") === tag) {
        return new Response(null, { status: 304, headers: { ETag: tag } });
      }

      return new Response(JSON.stringify({ items: matching, nextCursor: null }), { status: 200, headers: { "Content-Type": "application/json", ETag: tag } });
    }
    return null;
  };

  const reviewDecisions: Route = ({ request, url, body }) => {
    if (request.method === "POST" && url.pathname === "/api/v1/verification/items/approve") {
      // Bulk approval, all or nothing: every item at the version the reviewer saw (ADR-0037 §2).
      const wanted = ((body as { items?: { id: string; version: string }[] } | null)?.items ?? []);
      decisions.push({ action: "approve-many", ifMatch: null, body });
      if (wanted.some((w) => reviewItems.find((i) => i.id === w.id)?.version !== w.version)) {
        return problem(409, "request.version_conflict");
      }

      const approved = wanted.map((w) => {
        const index = reviewItems.findIndex((i) => i.id === w.id);
        const found = reviewItems[index];
        const decided = { ...(found ?? reviewItem()), status: "approved", version: nextVersion(found?.version ?? '"1"') };
        reviewItems[index] = decided;
        return decided;
      });
      return json(200, { batchId: "0192f0c1-0000-7000-8000-00000000eb01", items: approved });
    }

    const decisionMatch = /^\/api\/v1\/verification\/items\/([^/]+)\/(approve|reject|correct|second-approve)$/.exec(url.pathname);
    if (request.method === "POST" && decisionMatch) {
      // A decision on the version the reviewer saw (If-Match), else 409 (ADR-0037 §2).
      const index = reviewItems.findIndex((i) => i.id === decisionMatch[1]);
      const item = reviewItems[index];
      if (!item) {
        return problem(404, "verification.item_not_found");
      }

      decisions.push({ action: decisionMatch[2] ?? "", ifMatch: request.headers.get("If-Match"), body });
      if (request.headers.get("If-Match") !== item.version) {
        return problem(409, "request.version_conflict");
      }

      const status = { approve: "approved", reject: "rejected", correct: "corrected", "second-approve": "approved" }[decisionMatch[2] ?? "approve"] ?? "approved";
      const decided = { ...item, status, version: nextVersion(item.version) };
      reviewItems[index] = decided;
      if (decisionMatch[2] === "correct") {
        const next = { ...item, id: `${item.id.slice(0, -4)}ffff`, recordVersion: Number(item.recordVersion) + 1, status: correctionWaits ? "awaiting_second" : "approved" };
        reviewItems.push(next);
        return json(200, { corrected: decided, next });
      }

      return json(200, decided);
    }
    return null;
  };

  return [policies, reviewDecisions];
}
