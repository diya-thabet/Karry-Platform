import { db, type OutboxCommand } from './db';
import { canonicalJson, sha256Hex, toBase64, uuidv7 } from './crypto';
import { identity, sign } from './device';

export interface CaptureInput {
  assetCode: string;
  meterType: 'HOURS' | 'KM';
  meterStart: number;
  photo?: Blob;
}

/**
 * Captures a shift entry as an immutable signed command. Commands and photos are written
 * in ONE IndexedDB transaction, so a crash can never leave a command without its photo.
 */
export async function capture(input: CaptureInput): Promise<OutboxCommand> {
  const device = await identity();
  if (!device) throw new Error('NOT_ENROLLED');

  const photoHash = input.photo ? await sha256Hex(await input.photo.arrayBuffer()) : null;
  const payload = { assetCode: input.assetCode, meterType: input.meterType, meterStart: input.meterStart, photoHash };
  const canonicalPayload = canonicalJson(payload);
  const signature = toBase64(await sign(device.privateKey, canonicalPayload));
  const payloadHash = await sha256Hex(canonicalPayload);

  return db.transaction('rw', db.identity, db.outbox, db.blobs, async () => {
    const current = (await db.identity.get('device'))!;
    const command: OutboxCommand = {
      commandId: uuidv7(),
      deviceSeq: current.nextSeq,
      deviceTime: new Date().toISOString(),
      type: 'ShiftEntrySubmitted',
      payload,
      canonicalPayload,
      payloadHash,
      signature,
      photoHash,
      status: 'PENDING',
    };
    await db.identity.update('device', { nextSeq: current.nextSeq + 1 });
    if (input.photo && photoHash) await db.blobs.put({ hash: photoHash, data: input.photo });
    await db.outbox.add(command);
    return command;
  });
}

export async function pendingCount(): Promise<number> {
  return db.outbox.where('status').equals('PENDING').count();
}

export type CommandResult = { commandId: string; status: 'ACCEPTED' | 'DUPLICATE' | 'REJECTED'; reason?: string };

/**
 * Uploads PENDING commands in device-sequence order, in batches. Only commands the server
 * acknowledges (ACCEPTED or DUPLICATE) are marked SYNCED; anything else stays queued.
 */
export async function sync(endpoint: string, batchSize = 50): Promise<{ sent: number; acknowledged: number }> {
  const device = await identity();
  if (!device) throw new Error('NOT_ENROLLED');
  let sent = 0;
  let acknowledged = 0;
  for (;;) {
    const batch = await db.outbox.where('status').equals('PENDING').sortBy('deviceSeq');
    const chunk = batch.slice(0, batchSize);
    if (chunk.length === 0) break;
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ deviceId: device.deviceId, commands: chunk }),
    });
    if (!response.ok) throw new Error(`SYNC_HTTP_${response.status}`);
    const results = (await response.json()) as CommandResult[];
    const ackIds = results.filter((r) => r.status === 'ACCEPTED' || r.status === 'DUPLICATE').map((r) => r.commandId);
    await db.outbox.where('commandId').anyOf(ackIds).modify({ status: 'SYNCED' });
    sent += chunk.length;
    acknowledged += ackIds.length;
    if (ackIds.length < chunk.length) break; // stop on rejection; never loop forever
  }
  return { sent, acknowledged };
}
