import { AlertTriangle, CheckCircle2, Clock, XCircle } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../cn";

export type Status = "ok" | "warning" | "error" | "pending";

const styles: Record<Status, { icon: typeof CheckCircle2; className: string }> = {
  ok: { icon: CheckCircle2, className: "text-success" },
  warning: { icon: AlertTriangle, className: "text-warning" },
  error: { icon: XCircle, className: "text-danger" },
  pending: { icon: Clock, className: "text-text-muted" },
};

/** Status with icon AND text (never colour alone: colour-blind users, sunlight). */
export function StatusChip({ status, children }: { status: Status; children: ReactNode }) {
  const { icon: Icon, className } = styles[status];
  return (
    <span data-status={status} className={cn("inline-flex items-center gap-1 rounded-full border-[length:var(--karry-border-width)] border-current bg-surface px-2 py-0.5 text-sm font-semibold", className)}>
      <Icon aria-hidden className="size-4" />
      {children}
    </span>
  );
}
