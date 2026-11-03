import { ChevronRight } from "lucide-react";
import { Children, type ReactNode } from "react";

/**
 * Where the page sits (UX-04, UX-05): the sections above it, as links, from the top down; the page's own title follows
 * in the header. The app passes its router's links (the design system does not know the router).
 */
export function Breadcrumbs({ label, children }: { label: string; children: ReactNode }) {
  const items = Children.toArray(children);
  return (
    <nav aria-label={label}>
      <ol className="flex flex-wrap items-center gap-1 text-sm text-text-muted">
        {items.map((item, index) => (
          <li key={index} className="flex items-center gap-1 [&_a]:rounded [&_a]:font-medium [&_a]:underline-offset-4 [&_a]:transition-colors [&_a:hover]:text-primary [&_a:hover]:underline">
            {index > 0 && <ChevronRight aria-hidden className="size-3.5 shrink-0 rtl:-scale-x-100" />}
            {item}
          </li>
        ))}
      </ol>
    </nav>
  );
}
