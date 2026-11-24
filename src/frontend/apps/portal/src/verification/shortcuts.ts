/** What a key does in the review queue (US-P2-VER-07 AC1, ADR-0038 §4). */
export type Shortcut = "approve" | "reject" | "correct" | "next" | "previous" | "help";

const keys: Record<string, Shortcut> = {
  a: "approve",
  r: "reject",
  c: "correct",
  j: "next",
  k: "previous",
  "?": "help",
};

/**
 * The shortcut a key press asks for, or null when it asks for none: never while the person types (a field, a list,
 * an editable area), never while a dialog or the review panel is open, never with Ctrl, Alt or Cmd (the browser's and
 * the screen reader's own keys stay theirs) — TC-P2-VER-011.
 */
export function shortcutOf(event: Pick<KeyboardEvent, "key" | "ctrlKey" | "metaKey" | "altKey" | "target">, root: Pick<Document, "querySelector">): Shortcut | null {
  if (event.ctrlKey || event.metaKey || event.altKey) {
    return null;
  }

  const target = event.target;
  if (target instanceof HTMLElement && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName) || target.closest("[role=combobox], [role=listbox]"))) {
    return null;
  }

  if (root.querySelector("[role=dialog], [role=alertdialog]")) {
    return null;
  }

  return keys[event.key.length === 1 ? event.key.toLowerCase() : event.key] ?? null;
}

/** The row a move lands on: J and K stop at the ends of the page instead of wrapping. */
export function moved(index: number, count: number, shortcut: "next" | "previous"): number {
  if (count === 0) {
    return -1;
  }

  return shortcut === "next" ? Math.min(index + 1, count - 1) : Math.max(index - 1, 0);
}
