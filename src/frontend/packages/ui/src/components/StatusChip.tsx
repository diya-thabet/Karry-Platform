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

/**
 * Status with icon AND text (never colour alone: colour-blind users, sunlight). `wrap` lets a sentence (an alert in a
 * table) go onto several lines instead of widening its column.
 */
export function StatusChip({ status, wrap = false, children }: { status: Status; wrap?: boolean; children: ReactNode }) {
  const { icon: Icon, className } = styles[status];
  return (
    <span data-status={status} className={cn(
      "inline-flex gap-1 px-2.5 py-0.5 text-xs font-semibold",
      wrap ? "items-start rounded-lg" : "items-center whitespace-nowrap rounded-full",
      // Outdoor: an outline too, as the soft backgrounds are white there.
      "[[data-theme=outdoor]_&]:border-2 [[data-theme=outdoor]_&]:border-current",
      className,
    )}>
      <Icon aria-hidden className={cn("size-3.5 shrink-0", wrap && "mt-px")} />
      {children}
    </span>
  );
}
