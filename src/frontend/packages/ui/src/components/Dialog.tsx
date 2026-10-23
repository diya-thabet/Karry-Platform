import * as RadixDialog from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import type { ReactNode } from "react";

/** Modal dialog (Radix: focus trap, Escape, focus returns to the trigger). Title and description are required for screen readers. */
export function Dialog(props: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description: ReactNode;
  closeLabel: string;
  children?: ReactNode;
  footer?: ReactNode;
}) {
  return (
    <RadixDialog.Root open={props.open} onOpenChange={props.onOpenChange}>
      <RadixDialog.Portal>
        <RadixDialog.Overlay className="fixed inset-0 z-40 bg-black/50" />
        <RadixDialog.Content className="fixed left-1/2 top-1/2 z-50 w-[min(32rem,calc(100vw-2rem))] -translate-x-1/2 -translate-y-1/2 rounded-lg border-[length:var(--karry-border-width)] border-border bg-surface p-6 text-text shadow-xl">
          <div className="flex items-start justify-between gap-4">
            <RadixDialog.Title className="text-xl font-bold">{props.title}</RadixDialog.Title>
            <RadixDialog.Close aria-label={props.closeLabel} className="min-h-[var(--karry-control-height)] min-w-[var(--karry-control-height)] rounded-md text-text-muted">
              <X aria-hidden className="mx-auto size-6" />
            </RadixDialog.Close>
          </div>
          <RadixDialog.Description className="mt-2 text-text-muted">{props.description}</RadixDialog.Description>
          {props.children && <div className="mt-4">{props.children}</div>}
          {props.footer && <div className="mt-6 flex flex-wrap justify-end gap-2">{props.footer}</div>}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}
