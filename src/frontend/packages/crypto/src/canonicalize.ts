/**
 * RFC 8785 JSON Canonicalization Scheme: the bytes a device signs, identical to the server's canonicalizer
 * (Karry.SharedKernel), both checked against tests/vectors/rfc8785.json. ECMAScript number serialization is the one
 * the RFC prescribes, so numbers go through JSON.stringify; object keys are sorted by UTF-16 code units.
 */
export function canonicalize(value: unknown): string {
  if (value === null || typeof value === "boolean" || typeof value === "string") {
    return JSON.stringify(value);
  }

  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw new TypeError("RFC 8785: NaN and Infinity are not JSON.");
    }

    return JSON.stringify(value);
  }

  if (Array.isArray(value)) {
    return `[${value.map((item: unknown) => canonicalize(item === undefined ? null : item)).join(",")}]`;
  }

  if (typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, item]) => item !== undefined)
      // Default sort compares UTF-16 code units, which is what the RFC requires (not locale order).
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${entries.map(([key, item]) => `${JSON.stringify(key)}:${canonicalize(item)}`).join(",")}}`;
  }

  throw new TypeError(`RFC 8785: a ${typeof value} is not JSON.`);
}
