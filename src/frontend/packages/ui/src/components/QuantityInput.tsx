import type { InputHTMLAttributes } from "react";
import { cn } from "../cn";
import { controlClasses, FieldFrame, useFieldIds, type FieldProps } from "./TextField";

/** Material state of a volume (D13): the same m³ weighs very differently banked, loose or compacted. */
export type MaterialState = "BANK" | "LOOSE" | "COMPACTED";

export interface QuantityInputProps extends FieldProps, Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "value" | "onChange"> {
  /** The typed text: kept as a string so no precision is lost (conversion and rules: units engine, P1.12). */
  value: string;
  onValueChange: (value: string) => void;
  /** Unit symbol shown next to the number and announced with it ("t", "m³", "h", "L"). */
  unit: string;
  unitLabel: string;
  state?: MaterialState;
  stateLabel?: string;
}

/**
 * Quantity entry shell (US-P1-OFF-05; logic in P1.12): a decimal keypad on tablets, the unit always visible and
 * announced, and for volumes the material state. A bare number is never shown without its unit (invariant).
 */
export function QuantityInput({ label, description, error, value, onValueChange, unit, unitLabel, state, stateLabel, className, ...props }: QuantityInputProps) {
  const ids = useFieldIds(error, description);
  return (
    <FieldFrame label={label} description={description} error={error} {...ids}>
      <div className="flex items-stretch gap-2">
        <input
          id={ids.id}
          className={cn(controlClasses, "text-right tabular-nums", className)}
          inputMode="decimal"
          autoComplete="off"
          value={value}
          onChange={(event) => { onValueChange(event.target.value); }}
          aria-describedby={ids.describedBy}
          aria-invalid={error ? true : undefined}
          {...props}
        />
        <span className="flex min-w-14 items-center justify-center rounded-md bg-background px-2 font-semibold text-text" aria-label={unitLabel}>
          {unit}
        </span>
      </div>
      {state && (
        <p className="text-sm font-semibold text-text-muted" data-material-state={state}>
          {stateLabel ?? state}
        </p>
      )}
    </FieldFrame>
  );
}
