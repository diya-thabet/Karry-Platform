import type { Invoice, Subscription } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { subscriptionOf } from "../factories";
import type { FakeState } from "../state";

/** Plans, and each company's subscription and invoices for the operator console. */
export function billingRoutes(s: FakeState): Route[] {
  const { tenantSubscriptions, tenantInvoices } = s;
  const billing: Route = ({ request, url, body }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/billing/plans") {
      return json(200, { items: ["starter", "professional", "business", "enterprise"].map((code) => ({ id: `plan-${code}`, code, version: 1, name: code, features: [], limits: {}, prices: [], publishedAt: "2026-09-01T00:00:00Z" })) });
    }

    const billed = /^\/api\/v1\/billing\/tenants\/([^/]+)\/(subscription|subscription\/actions|invoices|invoices\/([^/]+)\/payment)$/.exec(url.pathname);
    if (billed) {
      const [, tenantId = "", what, invoiceId] = billed;
      const subscription = tenantSubscriptions[tenantId] ?? (tenantSubscriptions[tenantId] = subscriptionOf());
      const invoices = tenantInvoices[tenantId] ?? (tenantInvoices[tenantId] = []);
      switch (true) {
        case request.method === "GET" && what === "subscription":
          return json(200, subscription);
        // As the API: every saved change (a state action included) is a new version; terms for an older one are refused (H-C1).
        case request.method === "PUT" && what === "subscription": {
          const ifMatch = request.headers.get("If-Match");
          if (ifMatch !== null && ifMatch !== subscription.version) {
            return problem(409, "request.version_conflict");
          }

          return json(200, (tenantSubscriptions[tenantId] = { ...subscription, ...(body as Partial<Subscription>), planVersion: 1, version: nextVersion(subscription.version) }));
        }
        case request.method === "POST" && what === "subscription/actions": {
          const status = { activate: "active", "missed-payment": "past_due", payment: "active", cancel: "cancelled" }[(body as { action: string }).action] ?? subscription.status;
          return json(200, (tenantSubscriptions[tenantId] = {
            ...subscription, status, statusSince: "2026-10-29T08:00:00Z", unpaidSince: status === "past_due" ? "2026-10-29T08:00:00Z" : null, version: nextVersion(subscription.version),
          }));
        }
        case request.method === "GET" && what === "invoices":
          return json(200, { items: invoices });
        case request.method === "POST" && what === "invoices": {
          const input = body as Omit<Invoice, "id" | "paidOn">;
          if (invoices.some((invoice) => invoice.number === input.number)) {
            return problem(409, "billing.invoice_number_taken");
          }

          const invoice: Invoice = { ...input, id: `0192f0c1-0000-7000-8000-00000000f${String(invoices.length + 1).padStart(3, "0")}`, paidOn: null };
          invoices.unshift(invoice);
          return json(201, invoice);
        }
        case request.method === "POST" && invoiceId !== undefined: {
          const invoice = invoices.find((candidate) => candidate.id === invoiceId);
          if (invoice) {
            invoice.paidOn = (body as { paidOn: string }).paidOn;
          }

          return json(200, invoice);
        }
      }
    }
    return null;
  };

  return [billing];
}
