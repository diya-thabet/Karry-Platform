import { useId, type InputHTMLAttributes, type ReactNode } from "react";
import { cn } from "../cn";

export interface FieldProps {
  label: ReactNode;
  description?: ReactNode;
  error?: ReactNode;
}

/** Label, help text and error wired to the control (aria-describedby, aria-invalid): screen readers announce them. */
export function useFieldIds(error: ReactNode, description: ReactNode) {
  const id = useId();
  const describedBy = [description ? `${id}-description` : null, error ? `${id}-error` : null].filter(Boolean).join(" ");
  return { id, descriptionId: `${id}-description`, errorId: `${id}-error`, describedBy: describedBy || undefined };
}

export const controlClasses =
  "min-h-[var(--karry-control-height)] w-full rounded-md border-[length:var(--karry-border-width)] border-border bg-surface px-3 " +
  "text-text placeholder:text-text-muted disabled:cursor-not-allowed disabled:opacity-60 aria-[invalid=true]:border-danger";

export function FieldFrame(props: FieldProps & { id: string; descriptionId: string; errorId: string; required?: boolean | undefined; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={props.id} className="font-semibold text-text">
        {props.label}
        {props.required && <span aria-hidden className="text-danger"> *</span>}
      </label>
      {props.description && <p id={props.descriptionId} className="text-sm text-text-muted">{props.description}</p>}
      {props.children}
      {props.error && <p id={props.errorId} role="alert" className="font-semibold text-danger">{props.error}</p>}
    </div>
  );
}

export function TextField({ label, description, error, className, required, ...props }: FieldProps & InputHTMLAttributes<HTMLInputElement>) {
  const ids = useFieldIds(error, description);
  return (
    <FieldFrame label={label} description={description} error={error} required={required} {...ids}>
      <input
        id={ids.id}
        className={cn(controlClasses, className)}
        aria-describedby={ids.describedBy}
        aria-invalid={error ? true : undefined}
        required={required}
        {...props}
      />
    </FieldFrame>
  );
}
