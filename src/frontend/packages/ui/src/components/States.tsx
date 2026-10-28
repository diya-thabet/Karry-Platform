import { AlertOctagon, Copy, Inbox, Lock } from "lucide-react";
import { useState, type ReactNode } from "react";
import { useTranslation } from "@karry/i18n";
import { Button } from "./Button";

export function EmptyState({ title, children, action }: { title: ReactNode; children?: ReactNode; action?: ReactNode }) {
  return (
    <section className="flex flex-col items-center gap-3 rounded-xl border-[length:var(--karry-border-width)] border-dashed border-divider bg-surface px-6 py-10 text-center">
      <span className="flex size-12 items-center justify-center rounded-full bg-surface-muted">
        <Inbox aria-hidden className="size-6 text-text-muted" />
      </span>
      <h2 className="text-base font-semibold text-text">{title}</h2>
      {children && <div className="text-text-muted">{children}</div>}
      {action}
    </section>
  );
}

/**
 * Error with the support reference (US-P1-OPS-01 AC3): the correlation ID the API returned (X-Correlation-Id = trace
 * ID), shown and copyable so support finds the exact request.
 */
export function ErrorState({ correlationId, onRetry, bare = false }: {
  correlationId?: string | undefined;
  onRetry?: (() => void) | undefined;
  /** Only the reference and its copy button (inside a dialog that already says what happened). */
  bare?: boolean;
}) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  return (
    <section role="alert" className={bare ? "flex flex-col gap-3" : "flex flex-col gap-3 rounded-xl border-[length:var(--karry-border-width)] border-danger bg-danger-soft p-5"}>
      {!bare && (
        <h2 className="flex items-center gap-2 text-base font-semibold text-danger">
          <AlertOctagon aria-hidden className="size-5" />
          {t("common.error.title")}
        </h2>
      )}
      {correlationId && (
        <>
          {!bare && <p className="text-text">{t("common.error.body")}</p>}
          <p className="flex flex-wrap items-center gap-2">
            <span className="text-sm text-text-muted">{t("common.error.reference")}</span>
            <code className="rounded-md bg-surface-muted px-2 py-1 font-mono text-xs text-text" data-testid="correlation-id">{correlationId}</code>
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
    <section className="flex flex-col items-start gap-3 rounded-xl border-[length:var(--karry-border-width)] border-primary bg-primary-soft p-6">
      <h2 className="flex items-center gap-2 text-base font-semibold text-text">
        <Lock aria-hidden className="size-5 text-primary" />
        {title}
      </h2>
      {children && <div className="text-text-muted">{children}</div>}
      {action}
    </section>
  );
}
