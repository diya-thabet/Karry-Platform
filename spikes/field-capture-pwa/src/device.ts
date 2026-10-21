import { db, type DeviceIdentity } from './db';
import { uuidv7 } from './crypto';

const ALGORITHM: EcKeyGenParams = { name: 'ECDSA', namedCurve: 'P-256' };

/**
 * Enrolls the device: generates an ECDSA P-256 key pair whose PRIVATE key is non-extractable.
 * Only the public key leaves the device (registered with the server during enrollment).
 */
export async function enroll(): Promise<{ deviceId: string; publicKeyJwk: JsonWebKey }> {
  const existing = await db.identity.get('device');
  if (existing) return { deviceId: existing.deviceId, publicKeyJwk: existing.publicKeyJwk };

  const pair = await crypto.subtle.generateKey(ALGORITHM, false, ['sign', 'verify']);
  const publicKeyJwk = await crypto.subtle.exportKey('jwk', pair.publicKey);
  const identity: DeviceIdentity = { id: 'device', deviceId: uuidv7(), privateKey: pair.privateKey, publicKeyJwk, nextSeq: 1 };
  await db.identity.put(identity);
  return { deviceId: identity.deviceId, publicKeyJwk };
}

export async function identity(): Promise<DeviceIdentity | undefined> {
  return db.identity.get('device');
}

export async function sign(privateKey: CryptoKey, canonicalPayload: string): Promise<ArrayBuffer> {
  return crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, privateKey, new TextEncoder().encode(canonicalPayload));
}
