import { test, expect } from '@playwright/test';
import { launchProfile, spike, waitForServiceWorker, FakeSyncServer, setNetwork } from './support';

// WP-P0-10. Desktop Chromium in CI; the 72 h / 2 GB tablet run stays a manual device-lab step.
const ENTRIES = Number(process.env.SPIKE_ENTRIES ?? 1000);
const PHOTOS_EVERY = 20;           // 1,000 entries → 50 photos in CI
const PHOTO_BYTES = 200 * 1024;    // ≈ compressed meter photo

test('TC-P0-OFF-001 app shell starts offline after the first visit', async () => {
  const { context, page } = await launchProfile();
  await waitForServiceWorker(page);
  await context.setOffline(true);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Karry field capture spike' })).toBeVisible();
  await context.close();
});

test('TC-P0-OFF-002 entries and photos captured offline survive a reload intact', async () => {
  const { context, page } = await launchProfile();
  await waitForServiceWorker(page);
  await spike(page, (api) => api.enroll());
  await context.setOffline(true);

  await spike(page, (api, a) => api.captureMany(a.n, a.bytes, a.every), { n: ENTRIES, bytes: PHOTO_BYTES, every: PHOTOS_EVERY });
  await page.reload();

  expect(await spike(page, (api) => api.pendingCount())).toBe(ENTRIES);
  expect(await spike(page, (api) => api.blobCount())).toBe(Math.ceil(ENTRIES / PHOTOS_EVERY));
  expect(await spike(page, (api) => api.blobBytes())).toBe(Math.ceil(ENTRIES / PHOTOS_EVERY) * PHOTO_BYTES);
  const seqs = (await spike(page, (api) => api.outbox())).map((c) => c.deviceSeq);
  expect(seqs).toEqual(Array.from({ length: ENTRIES }, (_, i) => i + 1)); // gap-free, ordered
  await context.close();
});

test('TC-P0-OFF-003 the outbox survives a full browser restart', async () => {
  const first = await launchProfile();
  await spike(first.page, (api) => api.enroll());
  await spike(first.page, (api) => api.captureMany(25));
  await first.context.close();

  const second = await launchProfile(first.dir);
  expect(await spike(second.page, (api) => api.pendingCount())).toBe(25);
  await second.context.close();
});

test('TC-P0-OFF-004 sync after reconnect stores every command exactly once', async () => {
  const { context, page } = await launchProfile();
  const { publicKeyJwk } = await spike(page, (api) => api.enroll());
  const server = new FakeSyncServer(publicKeyJwk);
  await server.attach(context);

  await setNetwork(context, server, false);
  await spike(page, (api) => api.captureMany(120));
  await expect(spike(page, (api) => api.sync('/sync/v1/batches'))).rejects.toThrow(); // offline: fails, nothing lost
  expect(await spike(page, (api) => api.pendingCount())).toBe(120);

  expect(server.requests).toBe(0);

  await setNetwork(context, server, true);
  const firstSync = await spike(page, (api) => api.sync('/sync/v1/batches'));
  expect(firstSync).toEqual({ sent: 120, acknowledged: 120 });
  expect(server.stored.size).toBe(120);
  expect(server.rejected).toBe(0);

  const secondSync = await spike(page, (api) => api.sync('/sync/v1/batches'));
  expect(secondSync).toEqual({ sent: 0, acknowledged: 0 }); // nothing re-sent once acknowledged
  expect(server.stored.size).toBe(120);
  await context.close();
});

test('TC-P0-OFF-005 a replayed batch is deduplicated by commandId', async () => {
  const { context, page } = await launchProfile();
  const { publicKeyJwk } = await spike(page, (api) => api.enroll());
  const server = new FakeSyncServer(publicKeyJwk);
  await server.attach(context);
  await spike(page, (api) => api.captureMany(10));
  const commands = await spike(page, (api) => api.outbox());

  await spike(page, (api) => api.sync('/sync/v1/batches'));
  // Simulate a lost acknowledgement: the device re-sends the same batch.
  const replay = await page.evaluate(async (cmds) => {
    const r = await fetch('/sync/v1/batches', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ commands: cmds }) });
    return r.json();
  }, commands);

  expect(replay.every((r: { status: string }) => r.status === 'DUPLICATE')).toBe(true);
  expect(server.stored.size).toBe(10);
  await context.close();
});
