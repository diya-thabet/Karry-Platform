import { createPublicKey, verify, createHash } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium, type BrowserContext, type Page } from '@playwright/test';

export const BASE_URL = 'http://localhost:4173';

type Spike = {
  enroll(): Promise<{ deviceId: string; publicKeyJwk: JsonWebKey }>;
  identity(): Promise<{ deviceId: string; publicKeyJwk: JsonWebKey; extractable: boolean; nextSeq: number } | null>;
  tryExportPrivateKey(): Promise<string>;
  captureMany(count: number, photoBytes?: number, photoEvery?: number): Promise<void>;
  pendingCount(): Promise<number>;
  outbox(): Promise<Array<{ commandId: string; deviceSeq: number; canonicalPayload: string; payloadHash: string; signature: string; status: string; photoHash: string | null }>>;
  blobCount(): Promise<number>;
  blobBytes(): Promise<number>;
  sync(endpoint: string): Promise<{ sent: number; acknowledged: number }>;
  status(): string | null;
};

/** Run a function of the in-page spike API. */
export async function spike<T>(page: Page, fn: (api: Spike, arg: any) => Promise<T> | T, arg?: unknown): Promise<T> {
  await page.waitForFunction(() => 'karrySpike' in window);
  return page.evaluate(([source, a]) => {
    const f = new Function(`return (${source})`)();
    return f((window as any).karrySpike, a);
  }, [fn.toString(), arg] as const);
}

/** A persistent Chromium profile: closing and relaunching it simulates an app/browser restart. */
export async function launchProfile(dir?: string): Promise<{ context: BrowserContext; page: Page; dir: string }> {
  const profileDir = dir ?? mkdtempSync(join(tmpdir(), 'karry-spike-'));
  const context = await chromium.launchPersistentContext(profileDir, { baseURL: BASE_URL });
  const page = context.pages()[0] ?? (await context.newPage());
  await page.goto('/');
  return { context, page, dir: profileDir };
}

/** Wait until the service worker controls the page (app shell cached for offline use). */
export async function waitForServiceWorker(page: Page): Promise<void> {
  await page.evaluate(async () => { await navigator.serviceWorker.ready; });
  if (!(await page.evaluate(() => !!navigator.serviceWorker.controller))) {
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller);
  }
}

export function verifySignature(publicKeyJwk: JsonWebKey, canonicalPayload: string, signatureB64: string): boolean {
  const key = createPublicKey({ key: publicKeyJwk as any, format: 'jwk' });
  return verify('sha256', Buffer.from(canonicalPayload), { key, dsaEncoding: 'ieee-p1363' }, Buffer.from(signatureB64, 'base64'));
}

/** Takes the device offline/online for both the browser and the fake server. */
export async function setNetwork(context: BrowserContext, server: FakeSyncServer, online: boolean): Promise<void> {
  server.online = online;
  await context.setOffline(!online);
}

export const sha256Hex = (text: string) => createHash('sha256').update(text).digest('hex');

/**
 * Fake sync server (stands in for POST /sync/v1/batches): verifies every device signature
 * and payload hash, deduplicates by commandId, and enforces per-device sequence order.
 */
export class FakeSyncServer {
  readonly stored = new Map<string, unknown>();
  requests = 0;
  /** Playwright routes bypass context.setOffline, so the fake server models reachability itself. */
  online = true;
  duplicates = 0;
  rejected = 0;
  private lastSeq = 0;

  constructor(private readonly publicKeyJwk: JsonWebKey) {}

  async attach(context: BrowserContext): Promise<void> {
    await context.route('**/sync/v1/batches', async (route) => {
      if (!this.online) { await route.abort('internetdisconnected'); return; }
      this.requests++;
      const body = route.request().postDataJSON() as { commands: Array<{ commandId: string; deviceSeq: number; canonicalPayload: string; payloadHash: string; signature: string }> };
      const results = body.commands.map((c) => {
        if (this.stored.has(c.commandId)) { this.duplicates++; return { commandId: c.commandId, status: 'DUPLICATE' }; }
        const valid = sha256Hex(c.canonicalPayload) === c.payloadHash && verifySignature(this.publicKeyJwk, c.canonicalPayload, c.signature);
        if (!valid) { this.rejected++; return { commandId: c.commandId, status: 'REJECTED', reason: 'INVALID_SIGNATURE' }; }
        if (c.deviceSeq !== this.lastSeq + 1) { this.rejected++; return { commandId: c.commandId, status: 'REJECTED', reason: 'MISSING_SEQUENCE' }; }
        this.lastSeq = c.deviceSeq;
        this.stored.set(c.commandId, c);
        return { commandId: c.commandId, status: 'ACCEPTED' };
      });
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(results) });
    });
  }
}
