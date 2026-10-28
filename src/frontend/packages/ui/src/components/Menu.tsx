import * as Dropdown from "@radix-ui/react-dropdown-menu";
import type { ReactNode } from "react";

export interface MenuItem {
  key: string;
  label: ReactNode;
  icon?: ReactNode;
  onSelect: () => void;
  /** Ticked (a current choice: language, theme). */
  checked?: boolean;
}

/** A menu behind a button (Radix: arrow keys, type-ahead, Escape; focus returns to the button). Items in groups. */
export function Menu(props: { trigger: ReactNode; label: string; groups: { label?: ReactNode; items: MenuItem[] }[]; align?: "start" | "end" }) {
  return (
    <Dropdown.Root>
      <Dropdown.Trigger asChild aria-label={props.label}>{props.trigger}</Dropdown.Trigger>
      <Dropdown.Portal>
        <Dropdown.Content
          align={props.align ?? "end"}
          sideOffset={6}
          className="z-50 min-w-56 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-1.5 text-sm text-text shadow-lg"
        >
          {props.groups.map((group, index) => (
            <Dropdown.Group key={index}>
              {index > 0 && <Dropdown.Separator className="my-1.5 h-px bg-divider" />}
              {group.label && <Dropdown.Label className="px-2.5 py-1.5 text-xs font-medium text-text-muted">{group.label}</Dropdown.Label>}
              {group.items.map((item) => (
                <Dropdown.Item
                  key={item.key}
                  onSelect={item.onSelect}
                  className="flex min-h-9 cursor-pointer select-none items-center gap-2 rounded-lg px-2.5 outline-none data-[highlighted]:bg-surface-muted"
                >
                  {item.icon}
                  <span className="flex-1">{item.label}</span>
                  {item.checked && <span aria-hidden className="text-primary">✓</span>}
                </Dropdown.Item>
              ))}
            </Dropdown.Group>
          ))}
        </Dropdown.Content>
      </Dropdown.Portal>
    </Dropdown.Root>
  );
}
