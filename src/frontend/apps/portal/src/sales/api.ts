import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";

export type Customer = components["schemas"]["CustomerResponse"];
export type CustomerInput = components["schemas"]["CustomerInput"];
export type Product = components["schemas"]["ProductResponse"];
export type ProductInput = components["schemas"]["ProductInput"];
export type PriceList = components["schemas"]["PriceListResponse"];
export type PriceListInput = components["schemas"]["PriceListInput"];
export type PriceQuote = components["schemas"]["PriceQuoteResponse"];

export const CustomersReadPermission = "sales.customers.read";
export const CustomersManagePermission = "sales.customers.manage";
export const ProductsReadPermission = "sales.products.read";
export const ProductsManagePermission = "sales.products.manage";
export const PricesManagePermission = "sales.prices.manage";

/** How a customer is invoiced and a product sold (ADR-0042): by mass, or by loose volume derived from the ticket's mass. */
export const saleUnits = ["t", "m3-loose"] as const;

export const customersKey = ["sales", "customers"] as const;
export const customerKey = (id: string) => ["sales", "customers", id] as const;
export const productsKey = ["sales", "products"] as const;
export const productKey = (id: string) => ["sales", "products", id] as const;
export const priceListsKey = ["sales", "price-lists"] as const;

/** The company's customers, filtered by status and a search on code, name or tax ID. */
export function useCustomers(status: "" | "active" | "blocked" = "", search = "") {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...customersKey, { status, search }],
    queryFn: async () => (await api.GET("/api/v1/sales/customers", {
      params: { query: { ...(status ? { status } : {}), ...(search.trim() ? { search: search.trim() } : {}) } },
    })).data?.items ?? [],
    enabled: can(CustomersReadPermission),
  });
}

/** The company's products; archived ones on request. */
export function useProducts(includeArchived = false) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...productsKey, { includeArchived }],
    queryFn: async () => (await api.GET("/api/v1/sales/products", { params: { query: { includeArchived } } })).data?.items ?? [],
    enabled: can(ProductsReadPermission),
  });
}

/** Price list versions, newest start first: of one site (or every site). */
export function usePriceLists(siteId: string) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...priceListsKey, { siteId }],
    queryFn: async () => (await api.GET("/api/v1/sales/price-lists", { params: { query: siteId ? { siteId } : {} } })).data?.items ?? [],
    enabled: can(ProductsReadPermission),
  });
}

/** A typed price as the API takes it ("12,5" → "12.5"), kept as text so no precision is lost; null unless positive with at most 4 decimals. */
export function priceText(text: string): string | null {
  const value = text.trim().replace(",", ".");
  return /^\d+(\.\d{1,4})?$/.test(value) && Number(value) > 0 ? value : null;
}
