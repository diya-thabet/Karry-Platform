import { Link } from "@tanstack/react-router";
import { Bell } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { useMyNotices } from "../notifications/api";

/** The header's bell (US-P2-NOT-01 AC1): how many notices are unread, as a badge and in its name; it opens the list. */
export function NotificationBell() {
  const { t } = useTranslation();
  const unread = useMyNotices().data?.unread ?? 0;
  return (
    <Link
      to="/notifications"
      aria-label={t("portal.notices.bell", { count: unread })}
      className="relative flex size-10 shrink-0 items-center justify-center rounded-lg text-text-muted transition-colors hover:bg-surface-muted hover:text-text"
    >
      <Bell aria-hidden className="size-5" />
      {unread > 0 && (
        <span aria-hidden className="absolute -end-0.5 -top-0.5 min-w-5 rounded-full bg-danger px-1 text-center text-xs font-semibold leading-5 text-white">
          {unread > 99 ? "99+" : unread}
        </span>
      )}
    </Link>
  );
}
