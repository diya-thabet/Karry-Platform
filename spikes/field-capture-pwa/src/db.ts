import Dexie, { type EntityTable } from 'dexie';

export type CommandStatus = 'PENDING' | 'SYNCED';

/** An immutable, signed field command waiting for (or acknowledged by) the server. */
export interface OutboxCommand {
  commandId: string;
  deviceSeq: number;
  deviceTime: string;
  type: 'ShiftEntrySubmitted';
  payload: Record<string, unknown>;
  canonicalPayload: string;
  payloadHash: string;
  signature: string; // base64, IEEE P1363 (r || s), as produced by WebCrypto
  photoHash: string | null;
  status: CommandStatus;
}

export interface StoredBlob {
  hash: string;
  data: Blob;
}

/** Device identity. The private key is a non-extractable CryptoKey (structured-cloned into IndexedDB). */
export interface DeviceIdentity {
  id: 'device';
  deviceId: string;
  privateKey: CryptoKey;
  publicKeyJwk: JsonWebKey;
  nextSeq: number;
}

export const db = new Dexie('karry-field-spike') as Dexie & {
  outbox: EntityTable<OutboxCommand, 'commandId'>;
  blobs: EntityTable<StoredBlob, 'hash'>;
  identity: EntityTable<DeviceIdentity, 'id'>;
};

db.version(1).stores({
  outbox: 'commandId, deviceSeq, status',
  blobs: 'hash',
  identity: 'id',
});
