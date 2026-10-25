import { AlertOctagon, Copy, Inbox, Lock } from "lucide-react";
import { useState, type ReactNode } from "react";
import { useTranslation } from "@karry/i18n";
import { Button } from "./Button";

export function EmptyState({ title, children, action }: { title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <section className="flex flex-col items-center gap-3 rounded-lg border-[length:var(--karry-border-width)] border-dashed border-border p-8 text-center">
      <Inbox aria-hidden className="size-10 text-text-muted" />
      <h2 className="text-lg font-bold text-text">{title}</h2>
      {children && <div className="text-text-muted">{children}</div>}
      {action}
    </section>
  );
}

/**
 * Error with the support reference (US-P1-OPS-01 AC3): the correlation ID the API returned (X-Correlation-Id = trace
 * ID), shown and copyable so support finds the exact request.
 */
export function ErrorState({ correlationId, onRetry }: { correlationId?: string | undefined; onRetry?: (() => void) | undefined }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  return (
    <section role="alert" className="flex flex-col gap-3 rounded-lg border-[length:var(--karry-border-width)] border-danger bg-surface p-6">
      <h2 className="flex items-center gap-2 text-lg font-bold text-danger">
        <AlertOctagon aria-hidden className="size-6" />
        {t("common.error.title")}
      </h2>
      {correlationId && (
        <>
          <p className="text-text">{t("common.error.body")}</p>
          <p className="flex flex-wrap items-center gap-2">
            <span className="text-text-muted">{t("common.error.reference")}</span>
            <code className="rounded bg-background px-2 py-1 font-mono text-text" data-testid="correlation-id">{correlationId}</code>
            <Button
              variant="secondary"
              onClick={() => {
                void navigator.clipboard.writeText(correlationId).then(() => { setCopied(true); });
              }}
            >
              <Copy aria-hidden className="size-4" />
              {copied ? t("common.error.copied") : t("common.error.copy")}
            </Button>
          </p>
        </>
      )}
      {onRetry && <Button onClick={onRetry} className="self-start">{t("common.retry")}</Button>}
    </section>
  );
}

/**
 * A feature the company's plan does not include (US-P1-BIL-03 AC2): what it brings and how to get it, instead of the
 * feature. Never shows the feature's data (the API refuses it anyway).
 */
export function UpgradeCard({ title, children, action }: { title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <section className="flex flex-col items-start gap-3 rounded-lg border-[length:var(--karry-border-width)] border-primary bg-surface p-6">
      <h2 className="flex items-center gap-2 text-lg font-bold text-text">
        <Lock aria-hidden className="size-5 text-primary" />
        {title}
      </h2>
      {children && <div className="text-text-muted">{children}</div>}
      {action}
    </section>
  );
}
