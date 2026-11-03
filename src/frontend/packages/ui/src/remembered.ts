/**
 * A person's choice remembered on this browser (theme, language, a reduced sidebar): a convenience, never data. Storage
 * may be missing or blocked (private browsing, a locked-down tablet): then nothing is remembered and nothing fails.
 */
export function rememberedChoice<T extends string>(key: string, accepts: (value: string | null) => value is T): T | null {
  try {
    const value = globalThis.localStorage.getItem(key);
    return accepts(value) ? value : null;
  } catch {
    return null;
  }
}

/** Remembers a choice on this browser; when storage is blocked the choice lasts for this page only. */
export function rememberChoice(key: string, value: string): void {
  try {
    globalThis.localStorage.setItem(key, value);
  } catch {
    // storage blocked: the choice lasts until the page is closed
  }
}
