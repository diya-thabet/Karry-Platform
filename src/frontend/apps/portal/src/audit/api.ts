import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";

export type AuditEntry = components["schemas"]["AuditEntryResponse"];
export type AuditChange = components["schemas"]["AuditChange"];
export type AuditActor = components["schemas"]["AuditActorResponse"];

export const AuditReadPermission = "audit.ledger.read";
export const AuditVerifyPermission = "audit.ledger.verify";

export const auditKey = ["audit"] as const;
export const latestVerificationKey = ["audit", "verifications", "latest"] as const;

/** The viewer's filters as the person sets them; empty means "any". Days are `yyyy-mm-dd` in the person's time zone. */
export interface AuditFilters {
  actor: string;
  entityType: string;
  entityId: string;
  siteId: string;
  eventType: string;
  fromDay: string;
  toDay: string;
}

export const noFilters: AuditFilters = { actor: "", entityType: "", entityId: "", siteId: "", eventType: "", fromDay: "", toDay: "" };

/** The start of a day where the person is, as the instant the API takes. */
export function dayStart(day: string): string {
  return new Date(`${day}T00:00:00`).toISOString();
}

/** The last instant of a day where the person is (the API's `to` is inclusive). */
export function dayEnd(day: string): string {
  return new Date(new Date(`${day}T00:00:00`).getTime() + 86_400_000 - 1).toISOString();
}

/** The query of `GET /audit/entries` and of its CSV export for these filters (only the ones set). */
export function auditQuery(filters: AuditFilters) {
  return {
    ...(filters.actor ? { actor: filters.actor } : {}),
    ...(filters.entityType ? { entityType: filters.entityType } : {}),
    ...(filters.entityId ? { entityId: filters.entityId } : {}),
    ...(filters.siteId ? { siteId: filters.siteId } : {}),
    ...(filters.eventType ? { eventType: filters.eventType } : {}),
    ...(filters.fromDay ? { from: dayStart(filters.fromDay) } : {}),
    ...(filters.toDay ? { to: dayEnd(filters.toDay) } : {}),
  };
}

/** A before or after value as the auditor reads it: JSON, or a dash when there was none. */
export function shownValue(value: unknown): string {
  return value === null || value === undefined ? "—" : typeof value === "string" ? value : JSON.stringify(value);
}

/** The fields the company's policy hid in an entry's event (they never appear in its changes either). */
export function maskedFieldsOf(entry: AuditEntry): string[] {
  const masked = (entry.event as { maskedFields?: unknown } | null)?.maskedFields;
  return Array.isArray(masked) ? masked.filter((field): field is string => typeof field === "string") : [];
}

/** The people in the trail the person may read, with their names (for the "person" filter). */
export function useAuditActors() {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...auditKey, "actors"],
    queryFn: async () => (await api.GET("/api/v1/audit/actors")).data?.actors ?? [],
    enabled: can(AuditReadPermission),
  });
}

/** The last integrity check of the company's trail and who ran it. */
export function useLatestVerification() {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: latestVerificationKey,
    queryFn: async () => (await api.GET("/api/v1/audit/verifications/latest")).data ?? { verification: null, verifiedBy: null, verifiedByName: null },
    enabled: can(AuditVerifyPermission),
  });
}
