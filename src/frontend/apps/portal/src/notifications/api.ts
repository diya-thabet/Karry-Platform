import { useQuery } from "@tanstack/react-query";
import { useRef } from "react";
import { ApiError, type components } from "@karry/api-client";
import type { useTranslation } from "@karry/i18n";
import { useApi } from "../app/api";

export type Notice = components["schemas"]["NotificationResponse"];
type TFunction = ReturnType<typeof useTranslation>["t"];

export const noticesKey = ["notifications", "mine"] as const;

/** The bell and the list ask again this often (ADR-0039 §3); an unchanged list is a 304. */
export const noticesPollMs = 30_000;

/** The notice types the portal writes in words (ADR-0039 §2). */
export const noticeTypes = ["review.rejected", "review.escalated", "asset.unsafe", "integrity.fraud_suspected", "integrity.missing_end"] as const;

/** The portal's pages a notice may open; another link (a tablet's page) is not followed here. */
const portalPages = ["/review", "/assets/holds", "/assets/integrity"] as const;

/** The page a notice opens in the portal, or null when it belongs to another app (the tablet's history). */
export function portalLink(link: string): (typeof portalPages)[number] | null {
  return portalPages.find((page) => page === link) ?? null;
}

/** A notice in the reader's words, from its type and the values it carries (never a sentence stored on the server). */
export function noticeText(t: TFunction, notice: Notice): string {
  const p = notice.parameters;
  switch (notice.type) {
    case "review.rejected":
      return t("portal.notice.review.rejected", { reason: t(`portal.review.reason.${p.reasonCode ?? "other"}`, { defaultValue: p.reasonCode ?? "" }) });
    case "review.escalated":
      return t("portal.notice.review.escalated", { dueAt: new Date(p.dueAt ?? notice.createdAt) });
    case "asset.unsafe":
      return t("portal.notice.asset.unsafe");
    case "integrity.fraud_suspected":
      return t("portal.notice.integrity.fraud_suspected");
    case "integrity.missing_end":
      return t("portal.notice.integrity.missing_end");
    default:
      return t("portal.notice.other");
  }
}

/**
 * The signed-in person's notifications and unread count (ADR-0039 §3), asked again every 30 s with the last answer's
 * `ETag`; a 304 keeps what is on screen (the API client throws on it, so it is caught here first).
 */
export function useMyNotices() {
  const api = useApi();
  const last = useRef<{ etag: string; data: { items: Notice[]; unread: number } } | null>(null);
  return useQuery({
    queryKey: noticesKey,
    queryFn: async () => {
      try {
        const answer = await api.GET("/api/v1/notifications/mine", { headers: last.current ? { "If-None-Match": last.current.etag } : {} });
        const data = { items: answer.data?.items ?? [], unread: Number(answer.data?.unread ?? 0) };
        const etag = answer.response.headers.get("ETag");
        last.current = etag ? { etag, data } : null;
        return data;
      } catch (error) {
        if (error instanceof ApiError && error.status === 304 && last.current) {
          return last.current.data;
        }

        throw error;
      }
    },
    refetchInterval: noticesPollMs,
  });
}
