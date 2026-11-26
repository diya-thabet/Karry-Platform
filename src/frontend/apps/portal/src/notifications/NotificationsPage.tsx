import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, EmptyState, ErrorState, PageHeader, StatusChip } from "@karry/ui";
import { useApi } from "../app/api";
import { noticesKey, noticeText, portalLink, useMyNotices, type Notice } from "./api";
import { BrowserAlerts } from "./BrowserAlerts";
import { NoticeChoices } from "./NoticeChoices";

/**
 * The person's notifications (US-P2-NOT-01 AC1, ADR-0039): newest first, unread marked, each in the reader's words;
 * opening one marks it read and goes where it points in the portal; all can be marked read at once. Below, how the
 * person is told: the channels they may turn off (ADR-0039 §2).
 */
export function NotificationsPage() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const notices = useMyNotices();
  const items = notices.data?.items ?? [];
  const at = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));

  const read = useMutation({
    mutationFn: async (notice: Notice) => (await api.POST("/api/v1/notifications/{notificationId}/read", { params: { path: { notificationId: notice.id } } })).data,
    onSettled: () => queryClient.invalidateQueries({ queryKey: noticesKey }),
  });
  const readAll = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/notifications/read-all")).data,
    onSettled: () => queryClient.invalidateQueries({ queryKey: noticesKey }),
  });

  const open = (notice: Notice) => {
    if (!notice.readAt) {
      read.mutate(notice);
    }

    const page = portalLink(notice.link);
    if (page) {
      void navigate({ to: page });
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={t("portal.notices.title")}
        description={t("portal.notices.description")}
        actions={(notices.data?.unread ?? 0) > 0 && (
          <Button variant="secondary" loading={readAll.isPending} onClick={() => { readAll.mutate(); }}>{t("portal.notices.readAll")}</Button>
        )}
      />
      {notices.isError && <ErrorState onRetry={() => void notices.refetch()} />}
      {notices.isSuccess && items.length === 0 && <EmptyState title={t("portal.notices.none")} />}
      {items.length > 0 && (
        <ul className="flex flex-col divide-y divide-divider rounded-xl border border-divider bg-surface" aria-label={t("portal.notices.title")}>
          {items.map((notice) => (
            <li key={notice.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
              {!notice.readAt && <StatusChip status="warning">{t("portal.notices.unread")}</StatusChip>}
              <span className={`min-w-0 flex-1 ${notice.readAt ? "text-text-muted" : "font-semibold"}`}>{noticeText(t, notice)}</span>
              <span className="text-sm text-text-muted">{at(notice.createdAt)}</span>
              {(portalLink(notice.link) !== null || !notice.readAt) && (
                <Button variant="secondary" onClick={() => { open(notice); }}>
                  {portalLink(notice.link) ? t("portal.notices.open") : t("portal.notices.markRead")}
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
      <NoticeChoices />
      <BrowserAlerts />
    </div>
  );
}
