/** base64url without padding (JWS, RFC 7515 §2). */
export function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function fromBase64Url(text: string): Uint8Array {
  return fromBase64(text.replace(/-/g, "+").replace(/_/g, "/"));
}

export function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary);
}

export function fromBase64(text: string): Uint8Array {
  const binary = atob(text.padEnd(text.length + ((4 - (text.length % 4)) % 4), "="));
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

export const utf8 = (text: string): Uint8Array<ArrayBuffer> => new TextEncoder().encode(text);
