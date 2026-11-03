import { Check } from "lucide-react";
import type { InputHTMLAttributes, ReactNode } from "react";

/**
 * A check box with its label (UX-17): the same look in every browser and theme, the whole label clickable (Fitts),
 * a native input underneath (keyboard, forms and screen readers work as usual).
 */
export function Checkbox({ label, className, ...props }: { label: ReactNode } & Omit<InputHTMLAttributes<HTMLInputElement>, "type">) {
  return (
    <label className={`inline-flex min-h-[var(--karry-control-height)] cursor-pointer items-center gap-3 text-[length:var(--karry-font-control)] text-text has-[:disabled]:cursor-not-allowed has-[:disabled]:opacity-60 ${className ?? ""}`}>
      <span className="relative inline-flex size-5 shrink-0">
        <input
          type="checkbox"
          className="peer size-5 cursor-[inherit] appearance-none rounded-md border-[length:var(--karry-border-width)] border-border bg-surface transition-colors checked:border-primary checked:bg-primary"
          {...props}
        />
        <Check aria-hidden strokeWidth={3} className="pointer-events-none absolute inset-0 m-auto size-3.5 text-primary-text opacity-0 transition-opacity peer-checked:opacity-100" />
      </span>
      <span>{label}</span>
    </label>
  );
}
