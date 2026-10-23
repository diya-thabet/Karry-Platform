import type { ReactNode } from "react";

/**
 * The standard admin screen (risk mitigation, plan §8.10): title + actions, a search, the list, and the selected
 * item's detail beside it (below it on narrow screens). Every admin screen uses this layout, so behaviour,
 * keyboard flow and accessibility are built and tested once.
 */
export function ListDetailPage(props: {
  title: ReactNode;
  actions?: ReactNode;
  search?: ReactNode;
  list: ReactNode;
  detail?: ReactNode;
  detailLabel: string;
}) {
  return (
    <div className="flex flex-col gap-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-bold text-text">{props.title}</h1>
        {props.actions && <div className="flex flex-wrap gap-2">{props.actions}</div>}
      </header>
      {props.search}
      <div className="grid gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div>{props.list}</div>
        {props.detail && (
          <section aria-label={props.detailLabel} className="rounded-lg border-[length:var(--karry-border-width)] border-border bg-surface p-4">
            {props.detail}
          </section>
        )}
      </div>
    </div>
  );
}
