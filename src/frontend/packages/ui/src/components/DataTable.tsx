import type { ReactNode } from "react";

export interface Column<T> {
  key: string;
  header: ReactNode;
  cell: (row: T) => ReactNode;
  /** Numbers and quantities are right-aligned with tabular digits. */
  numeric?: boolean;
}

/**
 * Accessible data table: real <table>, caption for screen readers, row selection by keyboard. On a phone each row is a
 * card (first column as its title, the others as "label: value" lines); the explicit roles keep the table semantics
 * that some browsers drop when a table is not displayed as one.
 */
export function DataTable<T>(props: {
  caption: string;
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  empty: ReactNode;
  selectedKey?: string | undefined;
  onSelect?: ((row: T) => void) | undefined;
}) {
  if (props.rows.length === 0) {
    return <>{props.empty}</>;
  }

  return (
    <div className="overflow-x-auto rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface shadow-[var(--karry-card-shadow)] max-sm:overflow-visible max-sm:rounded-none max-sm:border-0 max-sm:bg-transparent max-sm:shadow-none">
      <table role="table" className="w-full border-collapse text-left text-sm text-text max-sm:block">
        <caption className="sr-only">{props.caption}</caption>
        <thead role="rowgroup" className="bg-surface-muted max-sm:sr-only">
          <tr role="row">
            {props.columns.map((column) => (
              <th key={column.key} role="columnheader" scope="col" className={`px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-text-muted ${column.numeric ? "text-right" : ""}`}>{column.header}</th>
            ))}
          </tr>
        </thead>
        <tbody role="rowgroup" className="max-sm:flex max-sm:flex-col max-sm:gap-3">
          {props.rows.map((row) => {
            const key = props.rowKey(row);
            const selected = key === props.selectedKey;
            return (
              <tr
                key={key}
                role="row"
                aria-selected={props.onSelect ? selected : undefined}
                tabIndex={props.onSelect ? 0 : undefined}
                onClick={props.onSelect ? () => { props.onSelect?.(row); } : undefined}
                onKeyDown={props.onSelect ? (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); props.onSelect?.(row); } } : undefined}
                className={`border-t-[length:var(--karry-border-width)] border-divider transition-colors max-sm:block max-sm:rounded-xl max-sm:border-[length:var(--karry-border-width)] max-sm:bg-surface max-sm:px-4 max-sm:py-3 max-sm:shadow-[var(--karry-card-shadow)] ${props.onSelect ? "cursor-pointer hover:bg-surface-muted" : ""} ${selected ? "bg-primary-soft font-medium max-sm:bg-primary-soft" : ""}`}
              >
                {props.columns.map((column) => (
                  <td
                    key={column.key}
                    role="cell"
                    data-label={typeof column.header === "string" ? column.header : undefined}
                    className={`px-4 py-3 align-middle ${column.numeric ? "text-right tabular-nums" : ""} ${phoneCell}`}
                  >
                    {column.cell(row)}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/** On a phone: the first cell is the card's title, the others a line with the column's name before the value. */
const phoneCell =
  "max-sm:flex max-sm:items-center max-sm:justify-between max-sm:gap-4 max-sm:px-0 max-sm:py-1.5 max-sm:text-right " +
  "max-sm:before:text-left max-sm:before:text-xs max-sm:before:font-medium max-sm:before:uppercase max-sm:before:tracking-wide max-sm:before:text-text-muted max-sm:before:content-[attr(data-label)] " +
  "max-sm:first:mb-1 max-sm:first:block max-sm:first:border-b-[length:var(--karry-border-width)] max-sm:first:border-divider max-sm:first:pb-2.5 max-sm:first:text-left max-sm:first:text-base max-sm:first:font-semibold max-sm:first:before:content-none";
