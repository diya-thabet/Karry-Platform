import * as RadixDialog from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../cn";

const widths = {
  md: "w-[min(32rem,100vw)]",
  lg: "w-[min(48rem,100vw)]",
  xl: "w-[min(56rem,100vw)]",
} as const;

/**
 * A detail panel that slides in from the end edge (the right; the left in Arabic and N'Ko) over the page: an entry to
 * check, a record to read. Wide enough for photos side by side, with a visible title, a padded body that scrolls and
 * a footer that stays in view for the actions. A Radix dialog: focus stays inside, Escape and a click outside close
 * it, focus returns to the button that opened it. (The navigation on a phone is the `Drawer`.)
 */
export function Sheet(props: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: ReactNode;
  closeLabel: string;
  /** Next to the title: a status, a badge. */
  aside?: ReactNode;
  footer?: ReactNode;
  width?: keyof typeof widths;
  children?: ReactNode;
}) {
  return (
    <RadixDialog.Root open={props.open} onOpenChange={props.onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="karry-overlay fixed inset-0 z-40 bg-black/40 backdrop-blur-[2px]" />
        <RadixDialog.Content
          aria-describedby={props.description === undefined ? undefined : "karry-sheet-description"}
          className={cn(
            "karry-sheet fixed inset-y-0 end-0 z-50 flex max-w-full flex-col border-s-[length:var(--karry-border-width)] border-divider bg-surface text-text shadow-2xl",
            widths[props.width ?? "xl"],
          )}
        >
          <header className="flex shrink-0 items-start justify-between gap-4 border-b-[length:var(--karry-border-width)] border-divider px-6 py-4">
            <div className="flex min-w-0 flex-col gap-1">
              <div className="flex flex-wrap items-center gap-2">
                <RadixDialog.Title className="text-lg font-semibold">{props.title}</RadixDialog.Title>
                {props.aside}
              </div>
              {props.description !== undefined && (
                <RadixDialog.Description id="karry-sheet-description" className="text-sm text-text-muted">{props.description}</RadixDialog.Description>
              )}
            </div>
            <RadixDialog.Close aria-label={props.closeLabel} className="flex size-10 shrink-0 items-center justify-center rounded-lg text-text-muted transition-colors hover:bg-surface-muted">
              <X aria-hidden className="size-5" />
            </RadixDialog.Close>
          </header>
          <div className="flex-1 overflow-y-auto px-6 py-5">{props.children}</div>
          {props.footer !== undefined && (
            <footer className="flex shrink-0 flex-wrap items-center gap-2 border-t-[length:var(--karry-border-width)] border-divider bg-surface px-6 py-4">
              {props.footer}
            </footer>
          )}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
