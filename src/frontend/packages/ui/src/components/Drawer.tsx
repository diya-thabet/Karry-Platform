import * as RadixDialog from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import type { ReactNode } from "react";

/**
 * A panel that slides in from the start edge (the navigation on a phone). A Radix dialog: focus stays inside, Escape and
 * a tap outside close it, focus returns to the button that opened it. The title is for screen readers.
 */
export function Drawer(props: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  closeLabel: string;
  header?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <RadixDialog.Root open={props.open} onOpenChange={props.onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="karry-overlay fixed inset-0 z-40 bg-black/40 backdrop-blur-[2px]" />
        <RadixDialog.Content
          aria-describedby={undefined}
          className="karry-drawer fixed inset-y-0 start-0 z-50 flex w-[min(18rem,calc(100vw-3rem))] flex-col border-e-[length:var(--karry-border-width)] border-divider bg-surface text-text shadow-2xl"
        >
          <RadixDialog.Title className="sr-only">{props.title}</RadixDialog.Title>
          <div className="flex h-16 shrink-0 items-center justify-between gap-2 border-b-[length:var(--karry-border-width)] border-divider px-4">
            {props.header}
            <RadixDialog.Close aria-label={props.closeLabel} className="flex size-10 shrink-0 items-center justify-center rounded-lg text-text-muted transition-colors hover:bg-surface-muted">
              <X aria-hidden className="size-5" />
            </RadixDialog.Close>
          </div>
          <div className="flex-1 overflow-y-auto">{props.children}</div>
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
