import type { LucideIcon } from "lucide-react";
import type { HTMLAttributes, ReactNode } from "react";
import { cn } from "../cn";
import { Skeleton } from "./Skeleton";

/**
 * Every page's header (UX-06): breadcrumbs to the sections above it, the title with its status, a line under it and
 * the page's main actions at the end (below on narrow screens). The one `h1` of the page.
 */
export function PageHeader(props: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  /** {@link Breadcrumbs} of a page below a section. */
  breadcrumbs?: ReactNode;
  /** A status chip beside the title (active, archived). */
  status?: ReactNode;
  /** `translate="no"` for a title that is company data (a site's name). */
  titleTranslate?: HTMLAttributes<HTMLHeadingElement>["translate"];
  children?: ReactNode;
}) {
  return (
    <div className="flex flex-col gap-2">
      {props.breadcrumbs}
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-xl font-semibold tracking-tight text-text sm:text-2xl" translate={props.titleTranslate}>{props.title}</h1>
            {props.status}
          </div>
          {props.description && <p className="text-sm text-text-muted">{props.description}</p>}
          {props.children}
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

/** One figure on a dashboard: an icon, what it counts and the number (a skeleton until it is known, UX-14). */
export function StatCard(props: { icon: LucideIcon; label: ReactNode; value: ReactNode; hint?: ReactNode; children?: ReactNode }) {
  const Icon = props.icon;
  return (
    <div className="flex items-start gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4 shadow-[var(--karry-card-shadow)] transition-shadow hover:shadow-md sm:gap-4 sm:p-5">
      <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary-soft text-primary">
        <Icon aria-hidden className="size-5" />
      </span>
      <div className="flex min-w-0 flex-col">
        <span className="text-sm text-text-muted">{props.label}</span>
        {props.value === undefined
          ? <span aria-busy="true" className="flex h-8 items-center"><Skeleton className="h-6 w-12" /></span>
          : <span className="text-2xl font-semibold tabular-nums text-text">{props.value}</span>}
        {props.hint && <span className="text-xs text-text-muted">{props.hint}</span>}
        {props.children}
      </div>
    </div>
  );
}
