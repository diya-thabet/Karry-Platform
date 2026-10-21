import { db } from './db';
import { enroll, identity } from './device';
import { capture, pendingCount, sync, type CaptureInput } from './outbox';

async function render(): Promise<void> {
  const device = await identity();
  document.querySelector('#device-status')!.textContent = device ? `enrolled (${device.deviceId.slice(0, 8)}…)` : 'NOT_ENROLLED';
  document.querySelector('#outbox-status')!.textContent = `${await pendingCount()} pending`;
}

async function captureMany(count: number, photoBytes = 0, photoEvery = 0): Promise<void> {
  for (let i = 0; i < count; i++) {
    const input: CaptureInput = { assetCode: `CAT-966-${i % 40}`, meterType: 'HOURS', meterStart: 1000 + i };
    if (photoBytes > 0 && photoEvery > 0 && i % photoEvery === 0) {
      input.photo = new Blob([crypto.getRandomValues(new Uint8Array(Math.min(photoBytes, 65536))), new Uint8Array(Math.max(0, photoBytes - 65536))], { type: 'image/jpeg' });
    }
    await capture(input);
  }
  await render();
}

// Test/automation hook for the spike only (production code exposes nothing on window).
const api = {
  enroll: async () => { const r = await enroll(); await render(); return r; },
  identity: async () => { const d = await identity(); return d ? { deviceId: d.deviceId, publicKeyJwk: d.publicKeyJwk, extractable: d.privateKey.extractable, nextSeq: d.nextSeq } : null; },
  tryExportPrivateKey: async () => { const d = await identity(); try { await crypto.subtle.exportKey('jwk', d!.privateKey); return 'EXPORTED'; } catch (e) { return (e as Error).name; } },
  captureMany,
  pendingCount,
  outbox: () => db.outbox.orderBy('deviceSeq').toArray(),
  blobCount: () => db.blobs.count(),
  blobBytes: async () => (await db.blobs.toArray()).reduce((n, b) => n + b.data.size, 0),
  sync: async (endpoint: string) => { const r = await sync(endpoint); await render(); return r; },
  persist: async () => (navigator.storage?.persist ? navigator.storage.persist() : false),
  status: () => document.querySelector('#device-status')!.textContent,
};
(window as unknown as { karrySpike: typeof api }).karrySpike = api;

document.querySelector('#enroll')!.addEventListener('click', () => void api.enroll());
document.querySelector('#capture')!.addEventListener('click', () => void captureMany(1));
document.querySelector('#sync')!.addEventListener('click', () => void api.sync('/sync/v1/batches'));
void navigator.storage?.persist?.();
void render();
