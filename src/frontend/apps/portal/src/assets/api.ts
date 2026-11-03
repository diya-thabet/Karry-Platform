import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";
import { useSites } from "../sites/api";

export type Asset = components["schemas"]["AssetResponse"];
export type AssetInput = components["schemas"]["AssetInput"];
export type AssetChangeInput = components["schemas"]["AssetChangeInput"];
export type AssetType = components["schemas"]["AssetTypeResponse"];
export type Meter = components["schemas"]["MeterResponse"];
export type Reading = components["schemas"]["ReadingResponse"];

export const AssetsReadPermission = "assets.registry.read";
export const AssetsManagePermission = "assets.registry.manage";

export const assetStatuses = ["active", "in-repair", "idle", "disposed"] as const;
export const meterKinds = ["hours", "km"] as const;

/** Labels on one A4 sheet (3 × 8) and the most one print takes (ADR-0026). */
export const labelsPerSheet = 24;
export const maxLabels = 240;

export const assetsKey = ["assets"] as const;
export const assetKey = (id: string) => ["assets", id] as const;
export const metersKey = (assetId: string) => ["assets", assetId, "meters"] as const;
export const readingsKey = (assetId: string, meterId: string) => ["assets", assetId, "meters", meterId, "readings"] as const;

/** The machine type catalogue (the same for every company; the screens translate the codes). */
export function useAssetTypes() {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: ["assets", "types"],
    queryFn: async () => (await api.GET("/api/v1/assets/types")).data?.items ?? [],
    enabled: can(AssetsReadPermission),
    staleTime: Infinity,
  });
}

/**
 * A blob of the API (a photo, a label sheet) as a URL the page can show or download. Undefined where the browser
 * cannot make one (tests): the caller then shows nothing.
 */
export function blobUrl(blob: Blob): string | undefined {
  return typeof URL.createObjectURL === "function" ? URL.createObjectURL(blob) : undefined;
}

/** A reading as the person types it ("1 234,5" → "1234.5"): at most one decimal, else null. */
export function readingText(text: string): string | null {
  const value = text.trim().replace(/\s/g, "").replace(",", ".");
  return /^\d{1,7}(\.\d)?$/.test(value) ? value : null;
}

/** The site names the person sees, by ID (a site out of their reach shows as a dash). */
export function useSiteNames() {
  const sites = useSites(true);
  const names = new Map((sites.data ?? []).map((site) => [site.id, site.name]));
  return { sites: sites.data ?? [], name: (id: string) => names.get(id) ?? "—" };
}
