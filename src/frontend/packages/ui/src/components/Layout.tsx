import type { LucideIcon } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../cn";

/** A page's title with an optional line under it and its main actions on the right (wrap below on narrow screens). */
export function PageHeader(props: { title: ReactNode; description?: ReactNode; actions?: ReactNode; back?: ReactNode }) {
  return (
    <div className="flex flex-col gap-3">
      {props.back && <div className="text-sm">{props.back}</div>}
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-1">
          <h1 className="text-2xl font-semibold tracking-tight text-text">{props.title}</h1>
          {props.description && <p className="text-sm text-text-muted">{props.description}</p>}
        </div>
        {props.actions && <div className="flex flex-wrap gap-2">{props.actions}</div>}
      </div>
    </div>
  );
}

/** A content block: white card with a soft border, an optional header (title, line, actions) and its body. */
export function Card(props: {
  title?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  className?: string;
  /** Heading id, so the card can be the label of its section. */
  titleId?: string;
}) {
  return (
    <section
      aria-labelledby={props.titleId}
      className={cn("flex flex-col gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]", props.className)}
    >
      {(props.title ?? props.actions) && (
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="flex flex-col gap-0.5">
            {props.title && <h2 id={props.titleId} className="text-base font-semibold text-text">{props.title}</h2>}
            {props.description && <p className="text-sm text-text-muted">{props.description}</p>}
          </div>
          {props.actions && <div className="flex flex-wrap gap-2">{props.actions}</div>}
        </div>
      )}
      {props.children}
    </section>
  );
}

/** One figure on a dashboard: an icon, what it counts and the number (shown as "…" until known). */
export function StatCard(props: { icon: LucideIcon; label: ReactNode; value: ReactNode; hint?: ReactNode; children?: ReactNode }) {
  const Icon = props.icon;
  return (
    <div className="flex items-start gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
      <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary-soft text-primary">
        <Icon aria-hidden className="size-5" />
      </span>
      <div className="flex min-w-0 flex-col">
        <span className="text-sm text-text-muted">{props.label}</span>
        <span className="text-2xl font-semibold tabular-nums text-text">{props.value}</span>
        {props.hint && <span className="text-xs text-text-muted">{props.hint}</span>}
        {props.children}
      </div>
    </div>
  );
}
