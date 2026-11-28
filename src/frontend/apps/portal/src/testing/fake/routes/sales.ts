import type { Customer, CustomerInput, PriceListInput, Product, ProductInput } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { customer, priceList, product } from "../factories";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** Sales (P3.1): customers, products, price list versions and the price on a date. */
export function salesRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { customers, products, priceLists } = s;
  function salesRoute(method: string, url: URL, body: unknown, ifMatch: string | null): Response | null {
    const match = /^\/api\/v1\/sales\/(customers|products|price-lists|prices)(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const [, kind, id, action] = match;
    if (kind === "prices") {
      const quote = options.quotes?.[url.searchParams.get("productId") ?? ""];
      return quote ? json(200, quote) : problem(404, "sales.no_price");
    }

    if (kind === "customers") {
      if (!id && method === "GET") {
        const status = url.searchParams.get("status");
        const search = (url.searchParams.get("search") ?? "").toLowerCase();
        return json(200, {
          items: customers.filter((c) => (!status || c.status === status) && (!search || `${c.code} ${c.legalName} ${c.taxId ?? ""}`.toLowerCase().includes(search))),
        });
      }

      if (!id) {
        const input = body as CustomerInput;
        const created = customer({
          id: `0192f0c1-0000-7000-8000-${String(7000 + customers.length + 1).padStart(12, "0")}`, code: (input.code ?? "").toUpperCase(),
          legalName: input.legalName ?? "", taxId: input.taxId ?? null, paymentTermsDays: input.paymentTermsDays ?? 30, basis: input.basis ?? "t",
          addresses: input.addresses ?? [], contacts: input.contacts ?? [], allowedSiteIds: input.allowedSiteIds ?? [],
          creditLimit: input.creditLimit?.amount ? { amount: Number(input.creditLimit.amount), currency: input.creditLimit.currency ?? "EUR" } : null,
        });
        customers.push(created);
        return json(201, created);
      }

      const index = customers.findIndex((c) => c.id === id);
      const found = customers[index];
      if (!found) {
        return problem(404, "sales.customer_not_found");
      }

      const save = (next: Customer) => {
        customers[index] = { ...next, version: nextVersion(found.version) };
        return json(200, customers[index]);
      };
      if (method === "GET") {
        return json(200, found);
      }

      if (action === "block") {
        return save({ ...found, status: "blocked", blockedReason: (body as { reason: string }).reason });
      }

      if (action === "unblock") {
        return save({ ...found, status: "active", blockedReason: null });
      }

      if (ifMatch !== null && ifMatch !== found.version) {
        return problem(409, "request.version_conflict");
      }

      const input = body as CustomerInput;
      const limit = input.creditLimit;
      return save({
        ...found, legalName: input.legalName ?? "", taxId: input.taxId ?? null, paymentTermsDays: input.paymentTermsDays ?? 30, basis: input.basis ?? "t",
        addresses: input.addresses ?? [], contacts: input.contacts ?? [], allowedSiteIds: input.allowedSiteIds ?? [],
        creditLimit: limit ? (limit.amount ? { amount: Number(limit.amount), currency: limit.currency ?? "EUR" } : null) : (found.creditLimit ?? null),
      });
    }

    if (kind === "products") {
      if (!id && method === "GET") {
        const all = url.searchParams.get("includeArchived") === "true";
        return json(200, { items: products.filter((p) => all || p.status === "active") });
      }

      if (!id) {
        const input = body as ProductInput;
        const created = product({
          id: `0192f0c1-0000-7000-8000-${String(8000 + products.length + 1).padStart(12, "0")}`, code: (input.code ?? "").toUpperCase(),
          name: input.name ?? "", materialId: input.materialId ?? "", saleUnit: input.saleUnit ?? "t",
        });
        products.push(created);
        return json(201, created);
      }

      const index = products.findIndex((p) => p.id === id);
      const found = products[index];
      if (!found) {
        return problem(404, "sales.product_not_found");
      }

      const save = (next: Product) => {
        products[index] = { ...next, version: nextVersion(found.version) };
        return json(200, products[index]);
      };
      if (method === "GET") {
        return json(200, found);
      }

      if (action === "archive" || action === "restore") {
        return save({ ...found, status: action === "archive" ? "archived" : "active" });
      }

      if (ifMatch !== null && ifMatch !== found.version) {
        return problem(409, "request.version_conflict");
      }

      const input = body as ProductInput;
      return save({ ...found, name: input.name ?? "", materialId: input.materialId ?? found.materialId, saleUnit: input.saleUnit ?? found.saleUnit });
    }

    if (!id && method === "GET") {
      const siteId = url.searchParams.get("siteId");
      return json(200, { items: priceLists.filter((l) => !siteId || l.siteId === siteId) });
    }

    if (!id) {
      const input = body as PriceListInput;
      const created = priceList({
        id: `0192f0c1-0000-7000-8000-${String(9000 + priceLists.length + 1).padStart(12, "0")}`, siteId: input.siteId ?? "", customerId: input.customerId ?? null,
        currency: input.currency ?? "EUR", effectiveFrom: input.effectiveFrom ?? "2026-12-01", inForce: false,
        lines: (input.lines ?? []).map((l) => ({ productId: l.productId ?? "", unitPrice: Number(l.unitPrice) })),
      });
      priceLists.push(created);
      return json(201, created);
    }

    const index = priceLists.findIndex((l) => l.id === id);
    const found = priceLists[index];
    if (!found) {
      return problem(404, "sales.price_list_not_found");
    }

    if (action === "withdraw") {
      priceLists[index] = { ...found, status: "withdrawn", version: nextVersion(found.version) };
      return json(200, priceLists[index]);
    }

    return json(200, found);
  }

  return [(ctx) => salesRoute(ctx.method, ctx.url, ctx.body, ctx.ifMatch)];
}
