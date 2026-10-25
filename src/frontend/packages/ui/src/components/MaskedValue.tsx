import { Lock } from "lucide-react";
import type { ReactNode } from "react";

/**
 * A value the company's policy hides from this person (US-P1-IAM-07 AC2): `•••` with a lock and the reason as a
 * tooltip and for screen readers. The API never sends the value, so there is nothing to reveal here.
 */
export function MaskedValue({ label }: { label: string }) {
  return (
    <span title={label} data-masked="" className="inline-flex items-center gap-1 text-text-muted">
      <Lock aria-hidden className="size-4" />
      <span aria-hidden>•••</span>
      <span className="sr-only">{label}</span>
    </span>
  );
}

/** An object from the API that may list masked fields (`maskedFields`, api-standards). */
export interface MaybeMasked {
  maskedFields?: readonly string[] | null;
}

/**
 * Shows `children` when the field came with the object, {@link MaskedValue} when the policy masked it, and nothing
 * when it is hidden (absent and not listed: the person does not even learn it exists).
 */
export function SensitiveValue({ object, field, value, label, children }: {
  object: MaybeMasked;
  field: string;
  value: unknown;
  label: string;
  children: ReactNode;
}) {
  if (object.maskedFields?.includes(field)) {
    return <MaskedValue label={label} />;
  }

  return value === undefined || value === null ? null : <>{children}</>;
}
