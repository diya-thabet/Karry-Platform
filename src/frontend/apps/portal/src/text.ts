/**
 * Small text helpers written as loops rather than regular expressions: a pattern such as `/-+$/` backtracks on long
 * runs of the character (super-linear time on hostile input), these never do.
 */

/** `value` without the repeated `char` at its end. */
export function trimEnd(value: string, char: string): string {
  let end = value.length;
  while (end > 0 && value[end - 1] === char) end--;
  return value.slice(0, end);
}

/** `value` without the repeated `char` at both ends. */
export function trim(value: string, char: string): string {
  let start = 0;
  while (start < value.length && value[start] === char) start++;
  return trimEnd(value.slice(start), char);
}

/**
 * A lowercase code from free text, at most `max` characters: accents removed, every run of other characters becomes
 * one hyphen, never a hyphen at either end. "Carrière de Sfax" → "carriere-de-sfax".
 */
export function slugify(text: string, max: number): string {
  const words = text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().replace(/[^a-z0-9]+/g, "-");
  return trimEnd(trim(words, "-").slice(0, max), "-");
}

/** Looks like an e-mail address: one @, something before it, a dot inside the part after it, no spaces. */
export function looksLikeEmail(value: string): boolean {
  if (/\s/.test(value)) return false;
  const parts = value.split("@");
  if (parts.length !== 2) return false;
  const [local = "", domain = ""] = parts;
  const dot = domain.indexOf(".", 1);
  return local.length > 0 && dot > 0 && dot < domain.length - 1;
}
