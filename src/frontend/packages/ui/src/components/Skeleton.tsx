import { useTranslation } from "@karry/i18n";
import { cn } from "../cn";

/**
 * A grey block where content is about to appear (UX-12, Doherty threshold): the page answers at once with its shape
 * instead of a blank or an "empty" box. Decorative: whoever shows it announces the wait (`aria-busy`, a hidden label).
 */
export function Skeleton({ className }: { className?: string }) {
  return <span aria-hidden className={cn("block animate-pulse rounded-md bg-surface-muted [[data-theme=outdoor]_&]:bg-divider/20", className)} />;
}

/** A page whose data is loading (UX-13): the shape of a header and two cards, announced as loading. */
export function PageSkeleton() {
  const { t } = useTranslation();
  return (
    <div role="status" aria-busy="true" className="flex flex-col gap-6">
      <span className="sr-only">{t("common.loading")}</span>
      <div className="flex flex-col gap-2">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-8 w-72 max-w-full" />
      </div>
      {[0, 1].map((card) => (
        <div key={card} className="flex flex-col gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5">
          <Skeleton className="h-5 w-48" />
          <div className="grid gap-4 md:grid-cols-2">
            {[0, 1, 2, 3].map((field) => <Skeleton key={field} className="h-[var(--karry-control-height)] w-full" />)}
          </div>
        </div>
      ))}
    </div>
  );
}
