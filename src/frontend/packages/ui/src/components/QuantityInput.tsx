import { useId, useState, type InputHTMLAttributes, type ReactNode } from "react";
import { cn } from "../cn";
import { controlClasses, FieldFrame, useFieldIds, type FieldProps } from "./TextField";

/** Material state of a volume (ADR-0010, API code): the same m³ weighs very differently banked, loose or compacted. */
export type MaterialState = "bank" | "loose" | "compacted";

/** A unit the person can pick: its API code, the symbol shown next to the number and the name announced with it. */
export interface QuantityUnit {
  value: string;
  symbol: string;
  label: string;
  /** Only m³ carries a material state, and it always does. */
  hasState?: boolean;
}

export interface QuantityStateOption {
  value: MaterialState;
  label: string;
}

/** A factor a conversion used ("Loose density", "1.6 t/m³"), shown on tap (US-P1-UNI-01 AC4). */
export interface QuantityFactor {
  name: string;
  label: ReactNode;
  value: ReactNode;
}

export interface QuantityInputProps extends FieldProps, Omit<InputHTMLAttributes<HTMLInputElement>, "type" | "value" | "onChange"> {
  /** The typed text: kept as a string so no precision is lost (the API takes decimals as text). */
  value: string;
  onValueChange?: (value: string) => void;
  /** The units offered; with one, it is shown fixed, with more, as a toggle. */
  units: readonly QuantityUnit[];
  /** The selected unit's `value`. */
  unit: string;
  onUnitChange?: (unit: string) => void;
  unitGroupLabel?: string;
  /** The states offered while the selected unit has one. */
  states?: readonly QuantityStateOption[];
  state?: MaterialState | undefined;
  onStateChange?: (state: MaterialState) => void;
  stateGroupLabel?: string;
  /** Factors behind a converted value; a button reveals them (none: no button). */
  factors?: readonly QuantityFactor[] | undefined;
  factorsLabel?: string;
}

const toggleClasses =
  "min-h-[var(--karry-control-height)] min-w-14 px-3 font-semibold first:rounded-l-md last:rounded-r-md " +
  "border-[length:var(--karry-border-width)] border-border -ml-[var(--karry-border-width)] first:ml-0 " +
  "bg-surface text-text aria-pressed:bg-primary aria-pressed:text-primary-text aria-pressed:border-primary disabled:cursor-not-allowed disabled:opacity-60";

function Toggle<T extends string>(props: { label: string | undefined; options: readonly { value: T; text: string; name: string }[]; selected: T | undefined; onSelect?: ((value: T) => void) | undefined; disabled?: boolean | undefined }) {
  return (
    <div role="group" aria-label={props.label} className="flex">
      {props.options.map((option) => (
        <button
          key={option.value}
          type="button"
          className={toggleClasses}
          aria-pressed={option.value === props.selected}
          aria-label={option.name}
          disabled={props.disabled}
          onClick={() => { props.onSelect?.(option.value); }}
        >
          {option.text}
        </button>
      ))}
    </div>
  );
}

/**
 * Quantity entry and display (US-P1-OFF-05, US-P1-UNI-01 AC4): a decimal keypad on tablets, the unit always visible
 * and announced, a unit toggle and for volumes the material state. A converted value can reveal the factors behind it
 * on tap. A bare number is never shown without its unit (invariant); converting is the caller's job (units engine).
 */
export function QuantityInput({
  label, description, error, value, onValueChange, units, unit, onUnitChange, unitGroupLabel, states, state, onStateChange, stateGroupLabel,
  factors, factorsLabel, className, readOnly, disabled, ...props
}: QuantityInputProps) {
  const ids = useFieldIds(error, description);
  const factorsId = useId();
  const [showFactors, setShowFactors] = useState(false);
  const selected = units.find((u) => u.value === unit) ?? units[0];
  const withState = selected?.hasState === true && states !== undefined && states.length > 0;

  return (
    <FieldFrame label={label} description={description} error={error} {...ids}>
      <div className="flex flex-wrap items-stretch gap-2">
        <input
          id={ids.id}
          className={cn(controlClasses, "min-w-24 flex-1 text-right tabular-nums", className)}
          inputMode="decimal"
          autoComplete="off"
          value={value}
          readOnly={readOnly}
          disabled={disabled}
          onChange={(event) => { onValueChange?.(event.target.value); }}
          aria-describedby={ids.describedBy}
          aria-invalid={error ? true : undefined}
          {...props}
        />
        {units.length > 1
          ? <Toggle label={unitGroupLabel} options={units.map((u) => ({ value: u.value, text: u.symbol, name: u.label }))} selected={selected?.value} onSelect={onUnitChange} disabled={disabled} />
          : selected && (
            <span className="flex min-w-14 items-center justify-center rounded-md bg-background px-2 font-semibold text-text" aria-label={selected.label}>
              {selected.symbol}
            </span>
          )}
      </div>
      {withState && (states.length > 1 && onStateChange
        ? <Toggle label={stateGroupLabel} options={states.map((s) => ({ value: s.value, text: s.label, name: s.label }))} selected={state} onSelect={onStateChange} disabled={disabled} />
        : <p className="text-sm font-semibold text-text-muted" data-material-state={state}>{states.find((s) => s.value === state)?.label ?? state}</p>)}
      {factors && factors.length > 0 && (
        <div className="flex flex-col gap-1">
          <button
            type="button"
            className="self-start text-sm font-semibold text-primary underline-offset-4 hover:underline"
            aria-expanded={showFactors}
            aria-controls={factorsId}
            onClick={() => { setShowFactors((shown) => !shown); }}
          >
            {factorsLabel}
          </button>
          {showFactors && (
            <dl id={factorsId} className="grid grid-cols-[auto_1fr] gap-x-4 text-sm">
              {factors.map((factor) => (
                <div key={factor.name} className="contents" data-factor={factor.name}>
                  <dt className="text-text-muted">{factor.label}</dt>
                  <dd className="tabular-nums">{factor.value}</dd>
                </div>
              ))}
            </dl>
          )}
        </div>
      )}
    </FieldFrame>
  );
}
