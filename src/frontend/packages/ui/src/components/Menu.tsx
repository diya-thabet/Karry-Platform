import * as Dropdown from "@radix-ui/react-dropdown-menu";
import { Check, ChevronRight } from "lucide-react";
import { useState, type ReactNode } from "react";
import { cn } from "../cn";

/** A command: sign out, open a page. */
export interface MenuAction {
  kind?: "action";
  key: string;
  label: ReactNode;
  icon?: ReactNode;
  onSelect: () => void;
}

/** One value of a {@link MenuChoice}. `lang` names the language the label is written in (a language's own name). */
export interface MenuOption {
  value: string;
  label: ReactNode;
  icon?: ReactNode;
  lang?: string;
}

/**
 * A setting with a few values (language, theme): one row that shows the current value and opens a submenu of radio
 * items, so the menu stays short however many values there are (UX-01, Hick's law) and screen readers hear which one
 * is chosen (UX-02).
 */
export interface MenuChoice {
  kind: "choice";
  key: string;
  label: ReactNode;
  icon?: ReactNode;
  value: string;
  options: MenuOption[];
  onValueChange: (value: string) => void;
}

export type MenuEntry = MenuAction | MenuChoice;

const itemClasses = "flex min-h-9 cursor-pointer select-none items-center gap-2 rounded-lg px-2.5 outline-none transition-colors data-[highlighted]:bg-surface-muted data-[state=open]:bg-surface-muted";
const panelClasses = "karry-pop z-50 min-w-56 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-1.5 text-sm text-text shadow-lg";

/**
 * A menu behind a button (Radix: arrow keys, type-ahead, Escape; focus returns to the button). Entries in groups
 * separated by a line; an optional header (who is signed in) above them.
 */
export function Menu(props: { trigger: ReactNode; label: string; header?: ReactNode; groups: { label?: ReactNode; items: MenuEntry[] }[]; align?: "start" | "end" }) {
  return (
    <Dropdown.Root>
      <Dropdown.Trigger asChild aria-label={props.label}>{props.trigger}</Dropdown.Trigger>
      <Dropdown.Portal>
        <Dropdown.Content align={props.align ?? "end"} sideOffset={6} collisionPadding={8} className={cn(panelClasses, "max-w-[calc(100vw-1rem)]")}>
          {props.header && <div className="px-2.5 py-2">{props.header}</div>}
          {props.groups.map((group, index) => (
            <Dropdown.Group key={index}>
              {(index > 0 || props.header) && <Dropdown.Separator className="my-1.5 h-px bg-divider" />}
              {group.label && <Dropdown.Label className="px-2.5 py-1.5 text-xs font-medium text-text-muted">{group.label}</Dropdown.Label>}
              {group.items.map((item) => (item.kind === "choice" ? <Choice key={item.key} choice={item} /> : (
                <Dropdown.Item key={item.key} onSelect={item.onSelect} className={itemClasses}>
                  {item.icon}
                  <span className="flex-1">{item.label}</span>
                </Dropdown.Item>
              )))}
            </Dropdown.Group>
          ))}
        </Dropdown.Content>
      </Dropdown.Portal>
    </Dropdown.Root>
  );
}

/**
 * A setting with its value (the language, the theme): its choices open inside the menu, under it, never in a second
 * panel beside it. On a phone a panel beside the menu had no room and landed off the screen (owner's review
 * 2026-11-26). A tap or Enter opens and closes it; the arrow towards the reading direction opens it, as a submenu's would.
 */
function Choice({ choice }: { choice: MenuChoice }) {
  const [open, setOpen] = useState(false);
  const current = choice.options.find((option) => option.value === choice.value);
  return (
    <>
      <Dropdown.Item
        className={itemClasses}
        aria-expanded={open}
        onSelect={(event) => { event.preventDefault(); setOpen(!open); }}
        onKeyDown={(event) => {
          const forward = getComputedStyle(event.currentTarget).direction === "rtl" ? "ArrowLeft" : "ArrowRight";
          if (event.key === forward) {
            event.preventDefault();
            setOpen(true);
          }
        }}
      >
        {choice.icon}
        <span className="flex-1">{choice.label}</span>
        {current && <span className="max-w-32 truncate text-xs text-text-muted" lang={current.lang}>{current.label}</span>}
        <ChevronRight aria-hidden className={cn("size-4 text-text-muted transition-transform rtl:-scale-x-100", open && "rotate-90 rtl:rotate-90")} />
      </Dropdown.Item>
      {open && (
        <Dropdown.RadioGroup value={choice.value} onValueChange={choice.onValueChange} aria-label={typeof choice.label === "string" ? choice.label : undefined} className="mb-1 ms-6 border-s-[length:var(--karry-border-width)] border-divider ps-1">
          {choice.options.map((option) => (
            <Dropdown.RadioItem key={option.value} value={option.value} lang={option.lang} className={cn(itemClasses, "data-[state=checked]:font-semibold")}>
              {option.icon}
              <span className="min-w-0 flex-1 truncate">{option.label}</span>
              <Dropdown.ItemIndicator><Check aria-hidden className="size-4 text-primary" /></Dropdown.ItemIndicator>
            </Dropdown.RadioItem>
          ))}
        </Dropdown.RadioGroup>
      )}
    </>
  );
}
