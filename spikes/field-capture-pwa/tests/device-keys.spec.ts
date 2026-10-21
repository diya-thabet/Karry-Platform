import { test, expect } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { launchProfile, spike, verifySignature, sha256Hex } from './support';

// WP-P0-11: non-extractable ECDSA P-256 device keys persisted in IndexedDB.

test('TC-P0-KEY-001 the device private key cannot be exported', async () => {
  const { context, page } = await launchProfile();
  await spike(page, (api) => api.enroll());
  const identity = await spike(page, (api) => api.identity());
  expect(identity!.extractable).toBe(false);
  expect(await spike(page, (api) => api.tryExportPrivateKey())).toBe('InvalidAccessError');
  await context.close();
});

test('TC-P0-KEY-002 the key survives 10 browser restarts and keeps signing verifiably', async () => {
  const first = await launchProfile();
  const { publicKeyJwk } = await spike(first.page, (api) => api.enroll());
  await first.context.close();

  for (let restart = 1; restart <= 10; restart++) {
    const { context, page } = await launchProfile(first.dir);
    const identity = await spike(page, (api) => api.identity());
    expect(identity!.publicKeyJwk).toEqual(publicKeyJwk);
    await spike(page, (api) => api.captureMany(1));
    const [latest] = (await spike(page, (api) => api.outbox())).slice(-1);
    expect(verifySignature(publicKeyJwk, latest!.canonicalPayload, latest!.signature)).toBe(true);
    await context.close();
  }
});

test('TC-P0-KEY-003 server-side verification accepts the canonical payload signature', async () => {
  const { context, page } = await launchProfile();
  const { publicKeyJwk } = await spike(page, (api) => api.enroll());
  await spike(page, (api) => api.captureMany(1));
  const [command] = await spike(page, (api) => api.outbox());

  expect(sha256Hex(command!.canonicalPayload)).toBe(command!.payloadHash);
  expect(verifySignature(publicKeyJwk, command!.canonicalPayload, command!.signature)).toBe(true);

  if (process.env.UPDATE_SIGNATURE_VECTOR === '1') {
    // Regenerates the cross-language fixture consumed by the .NET interop test.
    const vector = { generatedBy: 'Chromium WebCrypto (TC-P0-KEY-003)', publicKeyJwk, canonicalPayload: command!.canonicalPayload, payloadHash: command!.payloadHash, signature: command!.signature };
    writeFileSync('../signature-interop/fixtures/browser-vector.json', JSON.stringify(vector, null, 2) + '\n');
  }
  await context.close();
});

test('TC-P0-KEY-004 a tampered payload or signature fails verification', async () => {
  const { context, page } = await launchProfile();
  const { publicKeyJwk } = await spike(page, (api) => api.enroll());
  await spike(page, (api) => api.captureMany(1));
  const [command] = await spike(page, (api) => api.outbox());

  const tamperedPayload = command!.canonicalPayload.replace(/"meterStart":\d+/, '"meterStart":999999');
  expect(tamperedPayload).not.toBe(command!.canonicalPayload);
  expect(verifySignature(publicKeyJwk, tamperedPayload, command!.signature)).toBe(false);

  const sig = Buffer.from(command!.signature, 'base64');
  sig[10] = sig[10]! ^ 0xff;
  expect(verifySignature(publicKeyJwk, command!.canonicalPayload, sig.toString('base64'))).toBe(false);
  await context.close();
});

test('TC-P0-KEY-005 clearing site data removes the key and the app requires re-enrollment', async () => {
  const { context, page } = await launchProfile();
  await spike(page, (api) => api.enroll());
  const cdp = await context.newCDPSession(page);
  await cdp.send('Storage.clearDataForOrigin', { origin: new URL(page.url()).origin, storageTypes: 'all' });
  await page.reload();

  expect(await spike(page, (api) => api.identity())).toBeNull();
  await expect(page.locator('#device-status')).toHaveText('NOT_ENROLLED');
  await expect(spike(page, (api) => api.captureMany(1))).rejects.toThrow(/NOT_ENROLLED/);
  await context.close();
});
