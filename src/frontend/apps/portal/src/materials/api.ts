import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import type { MaterialState, QuantityStateOption, QuantityUnit } from "@karry/ui";
import { useAccess, useApi } from "../app/api";

export type Material = components["schemas"]["MaterialResponse"];
export type MaterialInput = components["schemas"]["MaterialInput"];
export type DensityVersion = components["schemas"]["DensityVersionResponse"];
export type DensityInput = components["schemas"]["DensityInput"];
export type Conversion = components["schemas"]["ConversionResponse"];

export const MaterialsReadPermission = "materials.catalog.read";
export const MaterialsManagePermission = "materials.catalog.manage";

export const materialKinds = ["raw-rock", "product"] as const;
export const materialStates: readonly MaterialState[] = ["bank", "loose", "compacted"];

export const materialsKey = ["materials"] as const;
export const materialKey = (id: string) => ["materials", id] as const;

/** The company's materials; archived ones on request. Nothing is asked without the permission. */
export function useMaterials(includeArchived = false) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...materialsKey, { includeArchived }],
    queryFn: async () => (await api.GET("/api/v1/materials", { params: { query: { includeArchived } } })).data?.items ?? [],
    enabled: can(MaterialsReadPermission),
  });
}

/** Units a material converts between (ADR-0025: volume and mass), as the quantity toggle offers them. */
export function quantityUnits(t: (key: string) => string): QuantityUnit[] {
  return [
    { value: "m3", symbol: "m³", label: t("units.m3"), hasState: true },
    { value: "t", symbol: "t", label: t("units.t") },
    { value: "kg", symbol: "kg", label: t("units.kg") },
    { value: "st", symbol: "st", label: t("units.st") },
  ];
}

export function stateOptions(t: (key: string) => string, states: readonly MaterialState[] = materialStates): QuantityStateOption[] {
  return states.map((state) => ({ value: state, label: t(`units.state.${state}`) }));
}

/** The states a density version knows (a product never in the bench has no bank density). */
export function statesOf(version: DensityVersion | undefined): MaterialState[] {
  return version ? materialStates.filter((state) => version[state] !== null) : [];
}

/** The version valid on a date (YYYY-MM-DD): the last one starting on or before it. */
export function versionOn(material: Material, date: string): DensityVersion | undefined {
  return material.densities.filter((v) => v.effectiveFrom <= date).at(-1);
}

/**
 * A typed decimal as the API takes it ("12,5" → "12.5"), kept as text so no precision is lost; null when it is not
 * a plain non-negative decimal.
 */
export function decimalText(text: string): string | null {
  const value = text.trim().replace(",", ".");
  return /^\d+(\.\d+)?$/.test(value) ? value : null;
}

/** A decimal text times 10^places, exactly (percent ↔ fraction): ("4.5", -2) → "0.045". */
export function shiftDecimal(text: string, places: number): string {
  const [whole = "0", fraction = ""] = text.split(".");
  let digits = whole + fraction;
  let point = whole.length + places;
  if (point < 0) {
    digits = "0".repeat(-point) + digits;
    point = 0;
  }

  digits = digits.padEnd(point, "0");
  const integer = digits.slice(0, point).replace(/^0+(?=\d)/, "") || "0";
  const rest = digits.slice(point).replace(/0+$/, "");
  return rest ? `${integer}.${rest}` : integer;
}

/** The loose density for a bank density and a swell factor (ρ_loose = ρ_bank / (1 + s)), to 4 decimals as the API keeps them. */
export function looseFromSwell(bank: string, swellPercent: string): string | null {
  const b = Number(bank);
  const s = Number(swellPercent) / 100;
  return Number.isFinite(b) && Number.isFinite(s) && b > 0 && s > -1 ? String(Math.round((b / (1 + s)) * 10_000) / 10_000) : null;
}

/** The swell factor in percent for bank and loose densities (s = ρ_bank / ρ_loose − 1), to 1 decimal. */
export function swellPercent(bank: string, loose: string): string | null {
  const b = Number(bank);
  const l = Number(loose);
  return Number.isFinite(b) && Number.isFinite(l) && b > 0 && l > 0 ? String(Math.round((b / l - 1) * 1000) / 10) : null;
}
