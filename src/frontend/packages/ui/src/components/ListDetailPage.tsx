import type { ReactNode } from "react";
import { PageHeader } from "./Layout";

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
  description?: ReactNode;
}) {
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={props.title} description={props.description} actions={props.actions} />
      {props.search}
      <div className="grid gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div>{props.list}</div>
        {props.detail && (
          <section aria-label={props.detailLabel} className="self-start rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] lg:sticky lg:top-20">
            {props.detail}
          </section>
        )}
      </div>
    </div>
  );
}
