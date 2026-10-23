/**
 * Pseudo-localization (US-P1-OFF-06 AC2): every translated string becomes accented, ~35 % longer and wrapped in
 * ⟦ ⟧, so a staging build shows at a glance (and tests assert) that no text is hard-coded and layouts survive longer
 * translations. ICU placeholders and plural syntax are left untouched.
 */
const accents: Record<string, string> = {
  a: "à", b: "ƀ", c: "ç", d: "ð", e: "é", f: "ƒ", g: "ĝ", h: "ĥ", i: "î", j: "ĵ", k: "ķ", l: "ļ", m: "ɱ",
  n: "ñ", o: "ö", p: "þ", q: "ǫ", r: "ŕ", s: "š", t: "ţ", u: "ü", v: "ṽ", w: "ŵ", x: "ẋ", y: "ý", z: "ž",
  A: "À", B: "Ɓ", C: "Ç", D: "Ð", E: "É", F: "Ƒ", G: "Ĝ", H: "Ĥ", I: "Î", J: "Ĵ", K: "Ķ", L: "Ļ", M: "Ṁ",
  N: "Ñ", O: "Ö", P: "Þ", Q: "Ǫ", R: "Ŕ", S: "Š", T: "Ţ", U: "Ü", V: "Ṽ", W: "Ŵ", X: "Ẋ", Y: "Ý", Z: "Ž",
};

export const pseudoOpen = "⟦";
export const pseudoClose = "⟧";

export function pseudoLocalize(text: string): string {
  let depth = 0;
  let out = "";
  for (const char of text) {
    if (char === "{") depth++;
    if (char === "}") depth = Math.max(0, depth - 1);
    out += depth === 0 ? (accents[char] ?? char) : char;
  }

  const padding = "~".repeat(Math.ceil(text.length * 0.35));
  return `${pseudoOpen}${out}${padding}${pseudoClose}`;
}
