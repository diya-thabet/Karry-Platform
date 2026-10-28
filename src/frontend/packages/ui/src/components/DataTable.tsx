import type { ReactNode } from "react";

export interface Column<T> {
  key: string;
  header: ReactNode;
  cell: (row: T) => ReactNode;
  /** Numbers and quantities are right-aligned with tabular digits. */
  numeric?: boolean;
}

/** Accessible data table: real <table>, caption for screen readers, row selection by keyboard. */
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
    <div className="overflow-x-auto rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface shadow-[var(--karry-card-shadow)]">
      <table className="w-full border-collapse text-left text-sm text-text">
        <caption className="sr-only">{props.caption}</caption>
        <thead className="bg-surface-muted">
          <tr>
            {props.columns.map((column) => (
              <th key={column.key} scope="col" className={`px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-text-muted ${column.numeric ? "text-right" : ""}`}>{column.header}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {props.rows.map((row) => {
            const key = props.rowKey(row);
            const selected = key === props.selectedKey;
            return (
              <tr
                key={key}
                aria-selected={props.onSelect ? selected : undefined}
                tabIndex={props.onSelect ? 0 : undefined}
                onClick={props.onSelect ? () => { props.onSelect?.(row); } : undefined}
                onKeyDown={props.onSelect ? (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); props.onSelect?.(row); } } : undefined}
                className={`border-t-[length:var(--karry-border-width)] border-divider ${props.onSelect ? "cursor-pointer hover:bg-surface-muted" : ""} ${selected ? "bg-primary-soft font-medium" : ""}`}
              >
                {props.columns.map((column) => (
                  <td key={column.key} className={`px-4 py-3 align-middle ${column.numeric ? "text-right tabular-nums" : ""}`}>{column.cell(row)}</td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
