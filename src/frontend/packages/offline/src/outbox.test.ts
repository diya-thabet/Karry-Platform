import "fake-indexeddb/auto";
import { fromBase64Url, generateDeviceKey, importPinKey, publicJwk, verify } from "@karry/crypto";
import {
  applyReceipts, applySubmissions, commandBody, enqueue, keepConfirmedBlobsMs, markUploaded, maxBlobBytes, nextBatch, openOfflineDb, outboxSummary,
  purgeBlobs, sha256Hex, uuidv7, type CommandReceipt, type KarryOfflineDb,
} from "./index";

let db: KarryOfflineDb;
let publicKey: CryptoKey;
let n = 0;
const at = new Date("2026-11-09T08:00:00Z");
const photo = (fill: number, size = 100) => new Uint8Array(size).fill(fill);

beforeEach(async () => {
  db = openOfflineDb(`outbox-${String(++n)}`);
  const keys = await generateDeviceKey();
  publicKey = keys.publicKey;
  await db.device.put({
    id: "this", deviceId: "d1", tenantId: "t1", siteId: "s1", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32).fill(1)), enrolledAt: at.toISOString(), clockOffsetMs: 0,
  });
});
afterEach(async () => { await db.delete(); });

const receipt = (commandId: string, status: CommandReceipt["status"], code: string | null = null, retryable = false): CommandReceipt =>
  ({ commandId, status, code, retryable, flags: [] });

describe("enqueue (US-P2-OFF-01)", () => {
  it("gives each command the next number, signs it as the server checks it and names its evidence in the payload", async () => {
    const first = await enqueue(db, { type: "test.item_recorded.v1", payload: { n: 1 } }, () => at.getTime());
    const second = await enqueue(db, { type: "test.item_recorded.v1", payload: { n: 2 }, evidence: [{ content: photo(7), contentType: "image/jpeg" }] }, () => at.getTime());

    expect([first.sequence, second.sequence]).toEqual([1, 2]);
    expect(second.payload).toEqual({ n: 2, evidence: [{ sha256: await sha256Hex(photo(7)), size: 100, contentType: "image/jpeg" }] });
    expect(second.deviceTime).toBe("2026-11-09T08:00:00.000Z");
    expect(second.status).toBe("pending");
    for (const command of [first, second]) {
      expect(await verify(publicKey, commandBody(command), fromBase64Url(command.signature) as Uint8Array<ArrayBuffer>)).toBe(true);
    }

    const blob = await db.blobs.get(await sha256Hex(photo(7)));
    expect(blob?.content).toEqual(photo(7));
    expect(blob?.uploadedAt).toBeNull();
    expect((await db.device.get("this"))?.lastSequence).toBe(2);
  });

  it("never gives one number twice when commands are recorded at once (TC-P2-SYNC-014)", async () => {
    const records = await Promise.all(Array.from({ length: 8 }, (_, i) => enqueue(db, { type: "test.item_recorded.v1", payload: { i } })));

    expect(records.map((r) => r.sequence).sort((a, b) => a - b)).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
    for (const record of records) {
      expect(await verify(publicKey, commandBody(record), fromBase64Url(record.signature) as Uint8Array<ArrayBuffer>)).toBe(true);
    }
  });

  it("refuses evidence over 1 MB or of another type and records nothing", async () => {
    await expect(enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(1, maxBlobBytes + 1), contentType: "image/jpeg" }] })).rejects.toThrow(RangeError);
    await expect(enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(1), contentType: "image/gif" as "image/png" }] })).rejects.toThrow(RangeError);

    expect(await db.outbox.count()).toBe(0);
    expect(await db.blobs.count()).toBe(0);
  });

  it("makes lowercase, time-ordered UUIDv7 command IDs", () => {
    const id = uuidv7(at.getTime());

    expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(id.replace(/-/g, "").slice(0, 12)).toBe(at.getTime().toString(16).padStart(12, "0"));
    expect(uuidv7(at.getTime() + 1) > uuidv7(at.getTime())).toBe(true);
  });
});

describe("nextBatch and receipts (ADR-0031 §3–§4)", () => {
  it("sends pending commands in order with their blobs not yet uploaded, and the missing ones the server asks for", async () => {
    const a = await enqueue(db, { type: "t.v1", payload: { n: 1 }, evidence: [{ content: photo(1), contentType: "image/jpeg" }] });
    const b = await enqueue(db, { type: "t.v1", payload: { n: 2 } });
    const c = await enqueue(db, { type: "t.v1", payload: { n: 3 } });
    await markUploaded(db, await sha256Hex(photo(1)), at);
    await applyReceipts(db, [receipt(a.commandId, "waiting"), receipt(b.commandId, "accepted"), receipt(c.commandId, "rejected", "test.payload_invalid")], at);

    expect((await nextBatch(db)).commands).toEqual([]);
    const resend = await nextBatch(db, [1, 2, 3]);
    expect(resend.commands.map((x) => x.sequence)).toEqual([1, 2]);
    expect(resend.commands[0]).toEqual({ commandId: a.commandId, sequence: 1, type: "t.v1", payload: a.payload, deviceTime: a.deviceTime, signature: a.signature });
    expect(resend.blobs).toEqual([]);
  });

  it("keeps a batch within its limits and lists the blobs to upload first", async () => {
    for (let i = 0; i < 4; i++) {
      await enqueue(db, { type: "t.v1", payload: { i }, evidence: [{ content: photo(i + 1), contentType: "image/webp" }] });
    }

    const batch = await nextBatch(db, [], { commands: 3, bytes: 1_000_000 });

    expect(batch.commands.map((c) => c.sequence)).toEqual([1, 2, 3]);
    expect(batch.blobs.map((b) => b.sha256).sort()).toEqual((await Promise.all([1, 2, 3].map((i) => sha256Hex(photo(i))))).sort());
  });

  it("sends a retryable rejection again, uploads the blob again on sync.blob_missing, and keeps a final one as rejected", async () => {
    const a = await enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(9), contentType: "image/png" }] });
    const b = await enqueue(db, { type: "t.v1", payload: {} });
    await markUploaded(db, await sha256Hex(photo(9)), at);

    await applyReceipts(db, [receipt(a.commandId, "rejected", "sync.blob_missing", true), receipt(b.commandId, "rejected", "sync.blob_mismatch")], at);

    const [again, refused] = [await db.outbox.get(a.commandId), await db.outbox.get(b.commandId)];
    expect([again?.status, again?.attempts, again?.code]).toEqual(["pending", 1, "sync.blob_missing"]);
    expect([refused?.status, refused?.code]).toEqual(["rejected", "sync.blob_mismatch"]);
    const batch = await nextBatch(db);
    expect(batch.commands.map((c) => c.commandId)).toEqual([a.commandId]);
    expect(batch.blobs.map((x) => x.sha256)).toEqual([await sha256Hex(photo(9))]);
    const summary = await outboxSummary(db);
    expect([summary.pending, summary.waiting, summary.rejected.map((r) => r.commandId), summary.lastAnswerAt]).toEqual([1, 0, [b.commandId], at.toISOString()]);
  });

  it("takes the final status of waiting commands from the pull; an unknown type is sent again", async () => {
    const a = await enqueue(db, { type: "t.v1", payload: {} });
    const b = await enqueue(db, { type: "t.v1", payload: {} });
    await applyReceipts(db, [receipt(a.commandId, "waiting"), receipt(b.commandId, "waiting")], at);

    await applySubmissions(db, [
      { id: a.commandId, sequence: 1, status: "conflict", code: null, flags: ["x.flag"], at: at.toISOString() },
      { id: b.commandId, sequence: 2, status: "rejected", code: "sync.type_unknown", flags: [], at: at.toISOString() },
    ], at);

    expect((await db.outbox.get(a.commandId))).toMatchObject({ status: "confirmed", flags: ["x.flag"] });
    expect((await db.outbox.get(b.commandId))?.status).toBe("pending");
  });
});

describe("evidence purge (ADR-0032 §6, TC-P2-SYNC-011)", () => {
  it("never frees a blob while a command naming it is not confirmed, then 7 days after, keeping its thumbnail", async () => {
    const evidence = [{ content: photo(5), contentType: "image/jpeg" as const, thumbnail: photo(6, 10) }];
    const a = await enqueue(db, { type: "t.v1", payload: { n: 1 }, evidence });
    const b = await enqueue(db, { type: "t.v1", payload: { n: 2 }, evidence });
    const hash = await sha256Hex(photo(5));
    const later = (days: number) => new Date(at.getTime() + days * 24 * 60 * 60 * 1000);

    await applyReceipts(db, [receipt(a.commandId, "accepted"), receipt(b.commandId, "waiting")], at);
    expect(await purgeBlobs(db, later(30))).toBe(0);
    expect((await db.blobs.get(hash))?.content).toEqual(photo(5));

    await applySubmissions(db, [{ id: b.commandId, sequence: 2, status: "accepted", code: null, flags: [], at: at.toISOString() }], at);
    expect(await purgeBlobs(db, new Date(at.getTime() + keepConfirmedBlobsMs - 1))).toBe(0);
    expect(await purgeBlobs(db, later(7))).toBe(1);

    const kept = await db.blobs.get(hash);
    expect(kept?.content).toBeNull();
    expect(kept?.thumbnail).toEqual(photo(6, 10));
  });

  it("keeps the evidence of a refused command and removes a purged blob without a thumbnail", async () => {
    const refused = await enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(3), contentType: "image/jpeg" }] });
    const sent = await enqueue(db, { type: "t.v1", payload: {}, evidence: [{ content: photo(4), contentType: "image/jpeg" }] });

    await applyReceipts(db, [receipt(refused.commandId, "rejected", "sync.blob_mismatch"), receipt(sent.commandId, "duplicate")], at);
    await purgeBlobs(db, new Date(at.getTime() + 2 * keepConfirmedBlobsMs));

    expect((await db.blobs.get(await sha256Hex(photo(3))))?.content).toEqual(photo(3));
    expect(await db.blobs.get(await sha256Hex(photo(4)))).toBeUndefined();
  });
});
