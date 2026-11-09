import "fake-indexeddb/auto";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { enqueue, openOfflineDb, sha256Hex, type KarryOfflineDb, type UploadedCommand } from "@karry/offline";
import { batchesPath, presignPath, pushOutbox, syncTablet, type WithLock } from "./uploads";

const photo = (fill: number) => new Uint8Array(64).fill(fill);
const noLock: WithLock = (work) => work();

interface Call { method: string; url: string; body: unknown; headers: Headers }

/**
 * A tablet's database with an enrolled device, and a fake server: presign answers a store URL (or `exists` for the
 * hashes in `stored`), the store takes any PUT, and a batch is answered by `answer` (by default everything accepted).
 */
async function setup() {
  const db = openOfflineDb(`uploads-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  await db.device.put({
    id: "this", deviceId: "d1", tenantId: "t1", siteId: "s1", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32)), enrolledAt: "2026-11-09T08:00:00Z", clockOffsetMs: 0,
  });
  const calls: Call[] = [];
  const stored = new Set<string>();
  const server = {
    answer: (commands: UploadedCommand[]) => ({
      status: 200,
      body: { results: commands.map((c) => ({ commandId: c.commandId, status: "accepted", code: null, retryable: false, flags: [] })), lastSequence: 0, missingSequences: [] as number[], serverTime: "2026-11-09T09:00:00Z" },
    }),
    fail: null as Error | null,
  };
  const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const respond = (input: string | URL | Request, init?: RequestInit): Response => {
    const url = input instanceof Request ? input.url : input.toString();
    const method = init?.method ?? "GET";
    const body = typeof init?.body === "string" ? JSON.parse(init.body) as unknown : init?.body;
    calls.push({ method, url, body, headers: new Headers(init?.headers) });
    if (url === presignPath) {
      const { sha256 } = body as { sha256: string };
      return json(200, stored.has(sha256)
        ? { exists: true, key: sha256, url: null, headers: null, expiresAt: null }
        : { exists: false, key: sha256, url: `http://store/${sha256}`, headers: { "Content-Type": "image/jpeg", "x-amz-checksum-sha256": "c" }, expiresAt: "x" });
    }

    if (url.startsWith("http://store/")) {
      stored.add(url.slice("http://store/".length));
      return new Response(null, { status: 200 });
    }

    if (url === batchesPath) {
      if (server.fail) {
        throw server.fail;
      }

      const answer = server.answer((body as { commands: UploadedCommand[] }).commands);
      return json(answer.status, answer.body);
    }

    throw new Error(`unexpected ${method} ${url}`);
  };
  const send = vi.fn((input: string | URL | Request, init?: RequestInit) => Promise.resolve().then(() => respond(input, init))) as unknown as typeof fetch;
  return { db, calls, stored, server, send };
}

const statuses = async (db: KarryOfflineDb) => (await db.outbox.orderBy("sequence").toArray()).map((c) => c.status);

describe("sending the outbox (US-P2-OFF-01, ADR-0032 §2)", () => {
  it("uploads the evidence to the store with the signed headers before the commands, then applies the answers", async () => {
    const { db, calls, send } = await setup();
    await enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(1), contentType: "image/jpeg" }] });
    await enqueue(db, { type: "t.v1", payload: {} });

    expect(await pushOutbox(db, send, noLock)).toBe("ok");

    const hash = await sha256Hex(photo(1));
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([`POST ${presignPath}`, `PUT http://store/${hash}`, `POST ${batchesPath}`]);
    expect(calls[0]?.body).toEqual({ sha256: hash, size: 64, contentType: "image/jpeg" });
    expect(calls[1]?.headers.get("x-amz-checksum-sha256")).toBe("c");
    expect(Array.from(calls[1]?.body as Uint8Array)).toEqual(Array.from(photo(1)));
    expect((calls[2]?.body as { commands: UploadedCommand[] }).commands.map((c) => c.sequence)).toEqual([1, 2]);
    expect(await statuses(db)).toEqual(["confirmed", "confirmed"]);
    expect((await db.blobs.get(hash))?.uploadedAt).not.toBeNull();
  });

  it("does not upload a blob the store already holds", async () => {
    const { db, calls, stored, send } = await setup();
    await enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(2), contentType: "image/jpeg" }] });
    stored.add(await sha256Hex(photo(2)));

    expect(await pushOutbox(db, send, noLock)).toBe("ok");

    expect(calls.map((c) => c.method)).toEqual(["POST", "POST"]);
  });

  it("sends again the numbers the server says are missing", async () => {
    const { db, calls, server, send } = await setup();
    const first = await enqueue(db, { type: "t.v1", payload: {} });
    const second = await enqueue(db, { type: "t.v1", payload: {} });
    await db.outbox.update(first.commandId, { status: "waiting" });
    let round = 0;
    server.answer = (commands) => ({
      status: 200,
      body: {
        results: commands.map((c) => ({ commandId: c.commandId, status: round === 0 ? "waiting" : "accepted", code: null, retryable: false, flags: [] })),
        lastSequence: round === 0 ? 0 : 2, missingSequences: round++ === 0 ? [1] : [], serverTime: "2026-11-09T09:00:00Z",
      },
    });

    expect(await pushOutbox(db, send, noLock)).toBe("ok");

    const sent = calls.filter((c) => c.url === batchesPath).map((c) => (c.body as { commands: UploadedCommand[] }).commands.map((x) => x.commandId));
    expect(sent).toEqual([[second.commandId], [first.commandId]]);
    expect(await statuses(db)).toEqual(["confirmed", "waiting"]);
  });

  it("loses nothing when the connection drops during the upload, and sends it all again later (TC-P2-SYNC-002)", async () => {
    const { db, server, send } = await setup();
    await enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(3), contentType: "image/jpeg" }] });
    server.fail = new TypeError("Failed to fetch");

    expect(await pushOutbox(db, send, noLock)).toBe("offline");
    expect(await statuses(db)).toEqual(["pending"]);

    server.fail = null;
    expect(await pushOutbox(db, send, noLock)).toBe("ok");
    expect(await statuses(db)).toEqual(["confirmed"]);
  });

  it("stops when the tablet is revoked or the server is busy", async () => {
    const { db, server, send } = await setup();
    await enqueue(db, { type: "t.v1", payload: {} });

    server.answer = () => ({ status: 403, body: { code: "devices.revoked" } as never });
    expect(await pushOutbox(db, send, noLock)).toBe("revoked");
    server.answer = () => ({ status: 429, body: { code: "request.rate_limited" } as never });
    expect(await pushOutbox(db, send, noLock)).toBe("busy");
    expect(await statuses(db)).toEqual(["pending"]);
  });

  it("runs one upload at a time across tabs, so a command is never sent twice at once (TC-P2-SYNC-014)", async () => {
    const { db, calls, send } = await setup();
    await enqueue(db, { type: "t.v1", payload: {} });
    let chain = Promise.resolve();
    const lock: WithLock = (work) => {
      const run = chain.then(work);
      chain = run.then(() => undefined, () => undefined);
      return run;
    };

    expect(await Promise.all([pushOutbox(db, send, lock), pushOutbox(db, send, lock)])).toEqual(["ok", "ok"]);

    expect(calls.filter((c) => c.url === batchesPath)).toHaveLength(1);
  });

  it("does not download after the upload learns the tablet is revoked", async () => {
    const { db, calls, server, send } = await setup();
    await enqueue(db, { type: "t.v1", payload: {} });
    server.answer = () => ({ status: 403, body: { code: "devices.revoked" } as never });

    expect(await syncTablet(db, send, noLock)).toBe("revoked");

    expect(calls.map((c) => c.url)).toEqual([batchesPath]);
  });
});
