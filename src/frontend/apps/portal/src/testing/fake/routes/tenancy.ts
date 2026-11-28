import type { Tenant } from "../types";
import { json, problem, type Route } from "../http";
import { operatorTenant } from "../factories";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** The operator console's companies and the company settings (logo included). */
export function tenancyRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { operatorTenants, changed } = s;
  const operatorConsole: Route = ({ request, url, body }) => {
    if (url.pathname === "/api/v1/tenancy/tenants" && options.operatorWithoutMfa?.() === true) {
      return problem(401, "auth.mfa_required");
    }

    if (url.pathname === "/api/v1/tenancy/tenants" && request.method === "GET") {
      const mrr = new Map<string, number>();
      for (const t of operatorTenants.filter((t) => t.mrr !== null && t.mrrCurrency)) {
        mrr.set(t.mrrCurrency ?? "", (mrr.get(t.mrrCurrency ?? "") ?? 0) + Number(t.mrr));
      }

      return json(200, {
        tenants: [...operatorTenants].sort((a, b) => a.name.localeCompare(b.name)),
        mrr: [...mrr].map(([currency, amount]) => ({ currency, amount })),
      });
    }

    if (url.pathname === "/api/v1/tenancy/tenants" && request.method === "POST") {
      const input = body as { slug: string; name: string; admin: { email: string } | null };
      const created = operatorTenant({ id: `0192f0c1-0000-7000-8000-0000000c${String(operatorTenants.length + 1).padStart(4, "0")}`, slug: input.slug, name: input.name, mrr: 0 });
      operatorTenants.push(created);
      return json(201, {
        id: created.id, slug: created.slug, name: created.name, createdAt: "2026-10-29T08:00:00Z",
        adminInvitation: input.admin ? { invited: true, delivered: options.invitationDelivered ?? true, error: null } : null,
      });
    }
    return null;
  };

  const settings: Route = ({ request, url, body }) => {
    if (url.pathname === "/api/v1/tenancy/settings" && request.method === "PUT") {
      const ifMatch = request.headers.get("If-Match");
      if (ifMatch !== null && ifMatch !== s.tenant.version) {
        return problem(409, "request.version_conflict");
      }

      return json(200, changed(body as Partial<Tenant>));
    }

    if (url.pathname === "/api/v1/tenancy/settings/logo") {
      return json(200, changed({ logoUrl: request.method === "PUT" ? `/api/v1/tenancy/public/${s.tenant.slug}/logo?v=${String(s.changes)}` : null }));
    }
    return null;
  };

  return [operatorConsole, settings];
}
