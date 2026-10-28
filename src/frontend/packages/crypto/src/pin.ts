import { fromBase64, toBase64 } from "./encoding";

/** What the reference data carries for a field user's PIN (ADR-0028 §4): never the server's hash. */
export interface PinVerifier {
  /** Salt of the server's PBKDF2 hash, base64. */
  salt: string;
  iterations: number;
  /** HMAC-SHA256(pinKey, PBKDF2-SHA256(pin, salt, iterations)), base64. */
  verifier: string;
}

/**
 * Imports the tablet's PIN key (received once at enrollment) as a non-extractable HMAC key: the verifiers are useless
 * without it, and the browser never gives it back.
 */
export async function importPinKey(raw: Uint8Array<ArrayBuffer>): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", raw, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
}

/** The verifier of a PIN, as the server computes it from its hash. Slow on purpose (PBKDF2, the server's iterations). */
export async function pinVerifier(pinKey: CryptoKey, pin: string, salt: string, iterations: number): Promise<string> {
  const material = await crypto.subtle.importKey("raw", new TextEncoder().encode(pin), "PBKDF2", false, ["deriveBits"]);
  const hash = await crypto.subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt: fromBase64(salt) as Uint8Array<ArrayBuffer>, iterations }, material, 256);
  return toBase64(new Uint8Array(await crypto.subtle.sign("HMAC", pinKey, hash)));
}

/** Whether a typed PIN matches; compares every byte (no early exit on the first difference). */
export async function checkPin(pinKey: CryptoKey, pin: string, expected: PinVerifier): Promise<boolean> {
  const actual = fromBase64(await pinVerifier(pinKey, pin, expected.salt, expected.iterations));
  const wanted = fromBase64(expected.verifier);
  let difference = actual.length ^ wanted.length;
  for (let i = 0; i < Math.min(actual.length, wanted.length); i++) {
    difference |= (actual[i] ?? 0) ^ (wanted[i] ?? 0);
  }

  return difference === 0;
}
