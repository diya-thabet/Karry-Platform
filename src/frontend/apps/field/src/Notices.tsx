import { Bell } from "lucide-react";
import { useEffect, useState } from "react";
import { useTranslation } from "@karry/i18n";
import { markNoticeRead, watchNotices, type KarryOfflineDb, type NoticeRow } from "@karry/offline";
import { Button } from "@karry/ui";

/**
 * The unlocked person's notices on the home screen (US-P2-NOT-01 AC1, TC-P2-NOT-001, ADR-0039 §3): a rejected entry,
 * with the reason, from the last pull. Another person's never show; "OK" marks one read on this tablet. Nothing shows
 * when there is nothing to say.
 */
export function Notices({ db, userId }: { db: KarryOfflineDb; userId: string }) {
  const { t } = useTranslation();
  const [notices, setNotices] = useState<NoticeRow[]>([]);
  useEffect(() => watchNotices(db, userId, setNotices), [db, userId]);

  if (notices.length === 0) {
    return null;
  }

  return (
    <section aria-label={t("field.notices.label")} className="flex flex-col gap-3 rounded-xl border-[length:var(--karry-border-width)] border-warning bg-warning-soft p-4">
      <h2 className="flex items-center gap-2 text-xl font-semibold">
        <Bell aria-hidden className="size-6" />
        {t("field.notices.label")}
      </h2>
      <ul className="flex flex-col gap-3">
        {notices.map((notice) => (
          <li key={notice.id} className="flex flex-wrap items-center justify-between gap-3">
            <span className="text-lg">{text(t, notice)}</span>
            <Button variant="secondary" onClick={() => void markNoticeRead(db, notice.id, new Date())} aria-label={t("field.notices.read", { text: text(t, notice) })}>
              {t("field.notices.ok")}
            </Button>
          </li>
        ))}
      </ul>
    </section>
  );
}

function text(t: ReturnType<typeof useTranslation>["t"], notice: NoticeRow): string {
  return notice.type === "review.rejected"
    ? t("field.notices.rejected", { reason: t(`field.history.reason.${notice.parameters.reasonCode ?? "other"}`, { defaultValue: notice.parameters.reasonCode ?? "" }) })
    : t("field.notices.other");
}
