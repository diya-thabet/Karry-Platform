import type { components } from "@karry/api-client";

export type VerifiedDocument = components["schemas"]["VerifiedDocument"];

/** The envelope and token of a verification address: `/verify/{envelopeId}#{token}` (ADR-0040 §4). */
export interface VerifyAddress {
  envelopeId: string;
  token: string;
}

/** True for the public page's paths: they render without sign-in. */
export function isVerifyPath(pathname: string): boolean {
  return /^\/verify\/[^/]+\/?$/.test(pathname);
}

/** The envelope from the path and the token from the fragment (never sent in the URL to the server). */
export function verifyAddress(location: Pick<Location, "pathname" | "hash">): VerifyAddress {
  const envelopeId = decodeURIComponent(location.pathname.replace(/^\/verify\//, "").replace(/\/$/, ""));
  return { envelopeId, token: location.hash.replace(/^#/, "") };
}

/** SHA-256 of a file, as the envelope keeps it (`sha256:` and lower-case hex): computed here, the file never leaves the device. */
export async function sha256Of(file: Blob): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  return "sha256:" + Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}
