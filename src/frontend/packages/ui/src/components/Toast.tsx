import * as RadixToast from "@radix-ui/react-toast";
import { AlertTriangle, CheckCircle2, Info, X, XCircle } from "lucide-react";
import { createContext, use, useCallback, useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "@karry/i18n";
import type { Status } from "./StatusChip";

/** The status is said by an icon as well as the colour (UX-15, WCAG 1.4.1). */
const icons: Record<Status, { icon: typeof Info; className: string }> = {
  ok: { icon: CheckCircle2, className: "text-success" },
  warning: { icon: AlertTriangle, className: "text-warning" },
  error: { icon: XCircle, className: "text-danger" },
  pending: { icon: Info, className: "text-primary" },
};

interface ToastMessage {
  id: number;
  status: Status;
  title: string;
  description?: string | undefined;
}

const ToastContext = createContext<((message: Omit<ToastMessage, "id">) => void) | null>(null);

/**
 * Toasts announced to screen readers (Radix: polite live region, swipe and Escape to dismiss, hover pauses), each with
 * its status icon and a close button.
 */
export function ToastProvider({ children, label }: { children: ReactNode; label: string }) {
  const { t } = useTranslation();
  const [messages, setMessages] = useState<ToastMessage[]>([]);
  const show = useCallback((message: Omit<ToastMessage, "id">) => {
    setMessages((current) => [...current, { ...message, id: Date.now() + Math.random() }]);
  }, []);
  const value = useMemo(() => show, [show]);

  return (
    <ToastContext value={value}>
      <RadixToast.Provider label={label} duration={6000}>
        {children}
        {messages.map((message) => {
          const { icon: Icon, className } = icons[message.status];
          return (
            <RadixToast.Root
              key={message.id}
              type={message.status === "error" ? "foreground" : "background"}
              onOpenChange={(open) => { if (!open) setMessages((current) => current.filter((m) => m.id !== message.id)); }}
              className="karry-toast flex items-start gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider border-s-4 border-s-success bg-surface p-4 text-text shadow-lg data-[status=error]:border-s-danger data-[status=warning]:border-s-warning data-[status=pending]:border-s-primary"
              data-status={message.status}
            >
              <Icon aria-hidden className={`mt-0.5 size-5 shrink-0 ${className}`} />
              <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                <RadixToast.Title className="text-sm font-semibold">{message.title}</RadixToast.Title>
                {message.description && <RadixToast.Description className="text-sm text-text-muted">{message.description}</RadixToast.Description>}
              </div>
              <RadixToast.Close aria-label={t("common.close")} className="-me-1 -mt-1 flex size-8 shrink-0 items-center justify-center rounded-lg text-text-muted transition-colors hover:bg-surface-muted hover:text-text">
                <X aria-hidden className="size-4" />
              </RadixToast.Close>
            </RadixToast.Root>
          );
        })}
        <RadixToast.Viewport className="fixed bottom-4 end-4 z-50 flex w-[min(24rem,calc(100vw-2rem))] flex-col gap-2" />
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
