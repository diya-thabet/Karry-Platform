import { describe, expect, it } from "vitest";
import { looksLikeEmail, slugify, trim, trimEnd } from "./text";

describe("text helpers", () => {
  it("trims a repeated character at the ends only", () => {
    expect(trimEnd("1.2500", "0")).toBe("1.25");
    expect(trimEnd("000", "0")).toBe("");
    expect(trim("--a-b--", "-")).toBe("a-b");
    expect(trim("", "-")).toBe("");
  });

  it("makes a code from free text, never ending on a hyphen after the cut", () => {
    expect(slugify("Carrière de Sfax", 40)).toBe("carriere-de-sfax");
    expect(slugify("  ¡Hola!  ", 40)).toBe("hola");
    expect(slugify("abc def", 4)).toBe("abc");
    expect(slugify("---", 10)).toBe("");
  });

  it("stays linear on long runs that made the old patterns backtrack", () => {
    const hostile = `${"-".repeat(50_000)}x${"-".repeat(50_000)}`;
    const started = performance.now();
    expect(slugify(hostile, 40)).toBe("x");
    expect(trimEnd(`1${"0".repeat(100_000)}`, "0")).toBe("1");
    expect(looksLikeEmail(`${"a".repeat(50_000)}@${"b.".repeat(25_000)}`)).toBe(true);
    expect(performance.now() - started).toBeLessThan(1_000);
  });

  it("accepts what looks like an e-mail address and nothing else", () => {
    expect(looksLikeEmail("amel@carriere.tn")).toBe(true);
    expect(looksLikeEmail("a@b.c")).toBe(true);
    for (const value of ["", "amel", "amel@", "@carriere.tn", "amel@carriere", "amel@carriere.", "a@.tn", "a b@c.tn", "a@b@c.tn"]) {
      expect(looksLikeEmail(value)).toBe(false);
    }
  });
});
