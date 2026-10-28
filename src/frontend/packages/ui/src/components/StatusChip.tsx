import { AlertTriangle, CheckCircle2, Clock, XCircle } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../cn";

export type Status = "ok" | "warning" | "error" | "pending";

const styles: Record<Status, { icon: typeof CheckCircle2; className: string }> = {
  ok: { icon: CheckCircle2, className: "bg-success-soft text-success" },
  warning: { icon: AlertTriangle, className: "bg-warning-soft text-warning" },
  error: { icon: XCircle, className: "bg-danger-soft text-danger" },
  pending: { icon: Clock, className: "bg-surface-muted text-text-muted" },
};

/** Status with icon AND text (never colour alone: colour-blind users, sunlight). */
export function StatusChip({ status, children }: { status: Status; children: ReactNode }) {
  const { icon: Icon, className } = styles[status];
  return (
    <span data-status={status} className={cn(
      "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2.5 py-0.5 text-xs font-semibold",
      // Outdoor: an outline too, as the soft backgrounds are white there.
      "[[data-theme=outdoor]_&]:border-2 [[data-theme=outdoor]_&]:border-current",
      className,
    )}>
      <Icon aria-hidden className="size-3.5" />
      {children}
    </span>
  );
}
