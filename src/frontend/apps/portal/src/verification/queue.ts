import { useQuery } from "@tanstack/react-query";
import { useRef } from "react";
import { ApiError, type ApiClient, type components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";
import { AssetsReadPermission } from "../assets/api";
import { ItemsReadPermission } from "./api";

export type ReviewItem = components["schemas"]["ReviewItemResponse"];
export type ShiftEntry = components["schemas"]["ShiftEntryResponse"];

/** A row of the queue: the item to decide and, when the person may read entries, the entry as it stands. */
export interface QueueRow {
  item: ReviewItem;
  entry: ShiftEntry | undefined;
}

/** What the queue shows: the site (empty for all), one flag code (empty for any), only the overdue items. */
export interface QueueFilters {
  siteId: string;
  flag: string;
  overdue: boolean;
}

export const queueKey = ["verification", "queue"] as const;
export const EntriesReadPermission = "fieldops.entries.read";

/** The queue asks again this often while the page is visible (ADR-0038 §3: a new synced entry within 10 s). */
export const queuePollMs = 5_000;

/** A page holds this many items; the console works the first page and fetches again as items are decided. */
export const queueSize = 50;

/** The flag codes an entry can carry (ADR-0034 §6), in the order the filter lists them. */
export const entryFlags = ["RULE-FO-01", "RULE-FO-02", "OVERLAP", "END_BEFORE_START", "SHIFT_OUTSIDE_PATTERN", "ASSET_NOT_ON_SITE", "ASSET_UNSAFE"] as const;

/** The words of a flag code, a key of the portal catalogue; an unknown code shows as it is. */
export function flagKey(code: string): string | null {
  return (entryFlags as readonly string[]).includes(code) ? `portal.review.flag.${code}` : null;
}

/**
 * One request for the page and one for its entries: the queue in risk order (ADR-0038 §1) with each entry as it
 * stands (`GET /fieldops/entries?ids=…`), never one request per row. Entries are skipped without the permission.
 */
export async function fetchQueue(api: ApiClient, filters: QueueFilters, readEntries: boolean, etag: string | undefined)
  : Promise<{ rows: QueueRow[]; etag: string | undefined } | "unchanged"> {
  let answer;
  try {
    answer = await api.GET("/api/v1/verification/items", {
      params: {
        query: {
          sort: "risk",
          limit: queueSize,
          ...(filters.siteId ? { siteId: filters.siteId } : {}),
          ...(filters.flag ? { flag: filters.flag } : {}),
          ...(filters.overdue ? { overdue: true } : {}),
        },
      },
      headers: etag ? { "If-None-Match": etag } : {},
    });
  } catch (error) {
    if (error instanceof ApiError && error.status === 304) {
      return "unchanged";
    }

    throw error;
  }

  const items = answer.data?.items ?? [];
  const ids = items.filter((item) => item.recordType === "fieldops.shift_entry").map((item) => item.recordId);
  const entries = readEntries && ids.length > 0
    ? (await api.GET("/api/v1/fieldops/entries", { params: { query: { ids, limit: 200 } } })).data?.items ?? []
    : [];
  const byId = new Map(entries.map((entry) => [entry.id, entry]));
  return { rows: items.map((item) => ({ item, entry: byId.get(item.recordId) })), etag: answer.response.headers.get("ETag") ?? undefined };
}

/**
 * The controller's queue (US-P2-VER-01): fetched again every 5 s while the page is visible, with the last page's
 * `ETag`; an unchanged page is a 304 and the rows on screen stay as they are.
 */
export function useReviewQueue(filters: QueueFilters) {
  const api = useApi();
  const { can } = useAccess();
  const last = useRef<{ filters: string; etag: string | undefined; rows: QueueRow[] } | null>(null);
  return useQuery({
    queryKey: [...queueKey, filters],
    queryFn: async () => {
      const key = JSON.stringify(filters);
      const previous = last.current?.filters === key ? last.current : null;
      const answer = await fetchQueue(api, filters, can(EntriesReadPermission), previous?.etag);
      if (answer === "unchanged" && previous) {
        return previous.rows;
      }

      const page = answer === "unchanged" ? { rows: [], etag: undefined } : answer;
      last.current = { filters: key, etag: page.etag, rows: page.rows };
      return page.rows;
    },
    refetchInterval: queuePollMs,
    enabled: can(ItemsReadPermission),
  });
}

/** Machine codes by ID, for the rows (the company's first 200 machines; another shows as a dash). */
export function useMachineCodes() {
  const api = useApi();
  const { can } = useAccess();
  const machines = useQuery({
    queryKey: ["assets", "codes"],
    queryFn: async () => (await api.GET("/api/v1/assets", { params: { query: { limit: 200 } } })).data?.items ?? [],
    enabled: can(AssetsReadPermission),
    staleTime: 5 * 60_000,
  });
  const codes = new Map((machines.data ?? []).map((asset) => [asset.id, asset.code]));
  return (id: string | null | undefined) => (id ? codes.get(id) ?? "—" : "—");
}

/**
 * Whether the signed-in reviewer's reviews know their field account (residual R-15, ADR-0037 §9): the queue warns when
 * a field account with their name exists that nobody linked. A failure shows no warning rather than an error dialog:
 * the server still checks separation of duties on every decision.
 */
export function useFieldAccountWarning(enabled: boolean) {
  const api = useApi();
  const answer = useQuery({
    queryKey: ["identity", "me", "field-account"],
    queryFn: async () => {
      try {
        return (await api.GET("/api/v1/identity/me/field-account")).data ?? null;
      } catch {
        return null;
      }
    },
    enabled,
    staleTime: 5 * 60_000,
  });
  return answer.data?.unlinkedNamesake === true;
}

