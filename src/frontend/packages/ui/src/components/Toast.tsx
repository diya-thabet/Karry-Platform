import * as RadixToast from "@radix-ui/react-toast";
import { createContext, use, useCallback, useMemo, useState, type ReactNode } from "react";
import type { Status } from "./StatusChip";

interface ToastMessage {
  id: number;
  status: Status;
  title: string;
  description?: string | undefined;
}

const ToastContext = createContext<((message: Omit<ToastMessage, "id">) => void) | null>(null);

/** Toasts announced to screen readers (Radix: polite live region, swipe and Escape to dismiss, hover pauses). */
export function ToastProvider({ children, label }: { children: ReactNode; label: string }) {
  const [messages, setMessages] = useState<ToastMessage[]>([]);
  const show = useCallback((message: Omit<ToastMessage, "id">) => {
    setMessages((current) => [...current, { ...message, id: Date.now() + Math.random() }]);
  }, []);
  const value = useMemo(() => show, [show]);

  return (
    <ToastContext value={value}>
      <RadixToast.Provider label={label} duration={6000}>
        {children}
        {messages.map((message) => (
          <RadixToast.Root
            key={message.id}
            type={message.status === "error" ? "foreground" : "background"}
            onOpenChange={(open) => { if (!open) setMessages((current) => current.filter((m) => m.id !== message.id)); }}
            className="rounded-xl border-[length:var(--karry-border-width)] border-divider border-l-4 border-l-success bg-surface p-4 text-text shadow-lg data-[status=error]:border-l-danger data-[status=warning]:border-l-warning data-[status=pending]:border-l-primary"
            data-status={message.status}
          >
            <RadixToast.Title className="text-sm font-semibold">{message.title}</RadixToast.Title>
            {message.description && <RadixToast.Description className="text-sm text-text-muted">{message.description}</RadixToast.Description>}
          </RadixToast.Root>
        ))}
        <RadixToast.Viewport className="fixed bottom-4 right-4 z-50 flex w-[min(24rem,calc(100vw-2rem))] flex-col gap-2" />
      </RadixToast.Provider>
    </ToastContext>
  );
}

export function useToast() {
  const show = use(ToastContext);
  if (!show) {
    throw new Error("useToast must be used inside <ToastProvider>.");
  }

  return show;
}
