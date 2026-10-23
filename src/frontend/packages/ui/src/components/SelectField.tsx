import * as Select from "@radix-ui/react-select";
import { Check, ChevronDown } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "../cn";
import { controlClasses, FieldFrame, useFieldIds, type FieldProps } from "./TextField";

export interface SelectOption {
  value: string;
  label: string;
}

/** Accessible select (Radix: keyboard, type-ahead, screen readers) with the standard field frame. */
export function SelectField(props: FieldProps & {
  options: SelectOption[];
  value: string | undefined;
  onValueChange: (value: string) => void;
  placeholder?: ReactNode;
  disabled?: boolean;
}) {
  const ids = useFieldIds(props.error, props.description);
  return (
    <FieldFrame label={props.label} description={props.description} error={props.error} {...ids}>
      <Select.Root value={props.value ?? ""} onValueChange={props.onValueChange} disabled={props.disabled ?? false}>
        <Select.Trigger
          id={ids.id}
          className={cn(controlClasses, "flex items-center justify-between gap-2 text-left")}
          aria-describedby={ids.describedBy}
          aria-invalid={props.error ? true : undefined}
        >
          <Select.Value placeholder={props.placeholder} />
          <Select.Icon><ChevronDown aria-hidden className="size-5" /></Select.Icon>
        </Select.Trigger>
        <Select.Portal>
          <Select.Content position="popper" sideOffset={4} className="z-50 min-w-[var(--radix-select-trigger-width)] rounded-md border-[length:var(--karry-border-width)] border-border bg-surface text-text shadow-lg">
            <Select.Viewport className="p-1">
              {props.options.map((option) => (
                <Select.Item
                  key={option.value}
                  value={option.value}
                  className="flex min-h-[var(--karry-control-height)] cursor-pointer select-none items-center gap-2 rounded px-3 outline-none data-[highlighted]:bg-primary data-[highlighted]:text-primary-text"
                >
                  <Select.ItemText>{option.label}</Select.ItemText>
                  <Select.ItemIndicator className="ml-auto"><Check aria-hidden className="size-4" /></Select.ItemIndicator>
                </Select.Item>
              ))}
            </Select.Viewport>
          </Select.Content>
        </Select.Portal>
      </Select.Root>
    </FieldFrame>
  );
}
