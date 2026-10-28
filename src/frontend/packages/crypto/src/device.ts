import { toBase64Url, utf8 } from "./encoding";

const algorithm = { name: "ECDSA", namedCurve: "P-256" } as const;
const signing = { name: "ECDSA", hash: "SHA-256" } as const;

/**
 * The tablet's key pair (ADR-0028 §2): ECDSA P-256 whose private key the browser never exports
 * (`extractable: false`), so a copied database cannot sign. Kept as a CryptoKey in IndexedDB.
 */
export async function generateDeviceKey(): Promise<CryptoKeyPair> {
  return crypto.subtle.generateKey(algorithm, false, ["sign", "verify"]);
}

/** The public key the server stores: the curve point of a P-256 key, as a JWK. */
export interface DevicePublicKey {
  kty: "EC";
  crv: "P-256";
  x: string;
  y: string;
}

/** The public key as a JWK, the only part that leaves the device. */
export async function publicJwk(key: CryptoKey): Promise<DevicePublicKey> {
  const { x, y } = await crypto.subtle.exportKey("jwk", key);
  if (!x || !y) {
    throw new TypeError("Not an elliptic-curve public key.");
  }

  return { kty: "EC", crv: "P-256", x, y };
}

/** Signs UTF-8 text: the signature is IEEE P1363 (r‖s, 64 bytes), what WebCrypto, JWS ES256 and the server use (D26). */
export async function sign(privateKey: CryptoKey, text: string): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.sign(signing, privateKey, utf8(text)));
}

export async function verify(publicKey: CryptoKey, text: string, signature: Uint8Array<ArrayBuffer>): Promise<boolean> {
  return crypto.subtle.verify(signing, publicKey, signature, utf8(text));
}

/** Proof at enrollment that the caller holds the private key of the public key it registers. */
export async function enrollmentProof(privateKey: CryptoKey, code: string): Promise<string> {
  return toBase64Url(await sign(privateKey, `karry-enroll:${code}`));
}

export interface DeviceRequest {
  deviceId: string;
  tenantId: string;
  method: string;
  /** Path of the request (no query string), e.g. /api/v1/sync/v1/changes. */
  path: string;
  /** Device time corrected by the last measured offset to the server's clock, in milliseconds. */
  now: number;
}

/** Seconds a device assertion is valid (the server refuses more). */
export const assertionLifetime = 300;

/**
 * The `Authorization: Device <jws>` value of one request (ADR-0028 §3): a compact JWS (ES256) naming the device, the
 * tenant, the method and path, valid 5 minutes, with a unique ID the server remembers to refuse replays.
 */
export async function deviceAssertion(privateKey: CryptoKey, request: DeviceRequest): Promise<string> {
  const iat = Math.floor(request.now / 1000);
  const header = { alg: "ES256", typ: "karry-device+jwt", kid: request.deviceId };
  const payload = {
    tid: request.tenantId, iat, exp: iat + assertionLifetime, jti: crypto.randomUUID(),
    htm: request.method.toUpperCase(), htu: request.path,
  };
  const signingInput = `${toBase64Url(utf8(JSON.stringify(header)))}.${toBase64Url(utf8(JSON.stringify(payload)))}`;
  return `${signingInput}.${toBase64Url(await sign(privateKey, signingInput))}`;
}
