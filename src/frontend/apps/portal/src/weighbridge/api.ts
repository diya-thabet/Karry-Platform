import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";

export type Truck = components["schemas"]["TruckResponse"];
export type TruckInput = components["schemas"]["TruckInput"];
export type Hauler = components["schemas"]["HaulerResponse"];
export type HaulerInput = components["schemas"]["HaulerInput"];

export const TrucksReadPermission = "weighbridge.trucks.read";
export const TrucksManagePermission = "weighbridge.trucks.manage";
/** Verifying a truck met at the desk closes its review flag (RULE-WB-09); not the weighmaster's. */
export const TrucksVerifyPermission = "weighbridge.trucks.verify";

export type TruckStatus = "active" | "unverified" | "archived";
export const truckStatuses: readonly TruckStatus[] = ["active", "unverified", "archived"];
export const tareMethods = ["weighed", "declared"] as const;

export const trucksKey = ["weighbridge", "trucks"] as const;
export const truckKey = (id: string) => ["weighbridge", "trucks", id] as const;
export const haulersKey = ["weighbridge", "haulers"] as const;

/** Trucks by any part of the plate (spaces, dashes and case ignored by the API), at most 50, exact plate first. */
export function useTrucks(plate = "", status: "" | TruckStatus = "") {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...trucksKey, { plate: plate.trim(), status }],
    queryFn: async () => (await api.GET("/api/v1/weighbridge/trucks", {
      params: { query: { ...(plate.trim() ? { plate: plate.trim() } : {}), ...(status ? { status } : {}) } },
    })).data?.items ?? [],
    enabled: can(TrucksReadPermission),
    placeholderData: (previous) => previous, // the list stays while the next keystroke's search runs
  });
}

/** The company's haulers by name; archived ones on request. */
export function useHaulers(includeArchived = false) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...haulersKey, { includeArchived }],
    queryFn: async () => (await api.GET("/api/v1/weighbridge/haulers", { params: { query: { includeArchived } } })).data?.items ?? [],
    enabled: can(TrucksReadPermission),
  });
}

/** A typed mass in whole kg ("14 200" → 14200); null when empty, undefined when it is not a positive whole number. */
export function kgFrom(text: string): number | null | undefined {
  const value = text.replace(/\s/g, "") /* \s covers no-break and narrow spaces too */;
  if (value === "") return null;
  return /^\d{1,6}$/.test(value) && Number(value) > 0 ? Number(value) : undefined;
}

/** No hauler chosen (a select cannot hold an empty value). */
export const noHauler = "none";

export interface TruckDraft { haulerId: string; tareKg: string; tareOn: string; tareMethod: string; maxPayloadKg: string; axles: string }

/** The input the API takes from a draft; the tare is sent only when it has a mass or a day (all three go together). */
export function truckInput(plate: string | null, draft: TruckDraft): TruckInput {
  const tareKg = kgFrom(draft.tareKg);
  const hasTare = draft.tareKg.trim() !== "" || draft.tareOn !== "";
  const payload = kgFrom(draft.maxPayloadKg);
  return {
    plate,
    haulerId: draft.haulerId === noHauler ? null : draft.haulerId,
    // An unreadable mass is sent as typed so the API answers with its own message on the field.
    tare: hasTare ? { kg: tareKg ?? (draft.tareKg.trim() || null), on: draft.tareOn || null, method: draft.tareMethod } : null,
    maxPayloadKg: payload === undefined ? draft.maxPayloadKg.trim() : payload,
    axles: draft.axles.trim() || null,
  };
}
