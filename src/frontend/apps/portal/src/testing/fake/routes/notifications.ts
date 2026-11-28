import { json, problem, type Route } from "../http";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** The person's notifications, Web Push, the company's rules and the person's channel choices (ADR-0039). */
export function notificationsRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { notices, noticeRules, choices, preferences } = s;
  const notifications: Route = ({ request, url, body }) => {
    if (url.pathname.startsWith("/api/v1/notifications/")) {
      // The person's own notifications (ADR-0039 §3): the list with an ETag and a 304, mark one, mark all.
      if (request.method === "GET" && url.pathname === "/api/v1/notifications/mine") {
        const unread = notices.filter((n) => n.readAt === null).length;
        const tag = `"${notices.map((n) => `${n.id}.${n.readAt === null ? "0" : "1"}`).join(";")}|${String(unread)}"`;
        return request.headers.get("If-None-Match") === tag
          ? new Response(null, { status: 304, headers: { ETag: tag } })
          : new Response(JSON.stringify({ items: notices, unread }), { status: 200, headers: { "Content-Type": "application/json", ETag: tag } });
      }

      if (request.method === "POST" && url.pathname === "/api/v1/notifications/read-all") {
        const count = notices.filter((n) => n.readAt === null).length;
        notices.forEach((n, index) => { notices[index] = { ...n, readAt: n.readAt ?? "2026-11-25T10:00:00Z" }; });
        return json(200, { count });
      }

      // Web Push (ADR-0039 §5): the key while on, and the person's browser subscriptions.
      if (url.pathname === "/api/v1/notifications/push/key") {
        return options.pushKey ? json(200, { publicKey: options.pushKey }) : problem(404, "notifications.push_off");
      }

      if (url.pathname === "/api/v1/notifications/push/subscription" || url.pathname === "/api/v1/notifications/push/subscription/remove") {
        return new Response(null, { status: 204 });
      }

      // The company's rule switches and the person's channel choices (ADR-0039 §2).
      if (request.method === "GET" && url.pathname === "/api/v1/notifications/rules") {
        return json(200, noticeRules);
      }

      if (request.method === "GET" && url.pathname === "/api/v1/notifications/preferences") {
        return json(200, preferences());
      }

      const ruleMatch = /^\/api\/v1\/notifications\/rules\/([^/]+)$/.exec(url.pathname);
      const rule = noticeRules.find((r) => r.type === decodeURIComponent(ruleMatch?.[1] ?? ""));
      if (request.method === "PUT" && rule) {
        rule.enabled = (body as { enabled: boolean }).enabled;
        rule.changedAt = "2026-11-26T09:00:00Z";
        rule.changedBy = "admin-1";
        return json(200, rule);
      }

      const choiceMatch = /^\/api\/v1\/notifications\/preferences\/([^/]+)\/([^/]+)$/.exec(url.pathname);
      const choice = choices[decodeURIComponent(choiceMatch?.[1] ?? "")];
      if (request.method === "PUT" && choiceMatch && choice) {
        const channel = decodeURIComponent(choiceMatch[2] ?? "");
        if (!(channel in choice)) {
          return problem(400, "notifications.channel_invalid");
        }

        choice[channel] = (body as { enabled: boolean }).enabled;
        return json(200, preferences());
      }

      const readMatch = /^\/api\/v1\/notifications\/([^/]+)\/read$/.exec(url.pathname);
      const index = notices.findIndex((n) => n.id === readMatch?.[1]);
      const found = notices[index];
      if (request.method === "POST" && found) {
        notices[index] = { ...found, readAt: found.readAt ?? "2026-11-25T10:00:00Z" };
        return json(200, notices[index]);
      }

      return problem(404, "notifications.not_found");
    }
    return null;
  };

  return [notifications];
}
