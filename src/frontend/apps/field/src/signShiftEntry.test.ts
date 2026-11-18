import "fake-indexeddb/auto";
import { generateDeviceKey, handwrittenHashes, importPinKey, payloadHash, publicJwk, utf8, verifyEnvelope, type SignatureEnvelopeV1 } from "@karry/crypto";
import { openOfflineDb, sha256Hex, type KarryOfflineDb } from "@karry/offline";
import type { Photo } from "./camera";
import { endCommand, startCommand } from "./meterState";
import { browserPosition, RecordRefused, signEnd, signStart, type SigningContext } from "./signShiftEntry";

let db: KarryOfflineDb;
let publicKey: CryptoKey;

async function photo(byte: number): Promise<Photo> {
  const content = new Uint8Array(64).fill(byte);
  return {
    content, contentType: "image/webp", thumbnail: new Uint8Array([byte]),
    capture: { sha256: await sha256Hex(content), capturedAt: "2026-11-09T06:02:11.250Z", deviceId: "d1", appVersion: "1.4.0", width: 1600, height: 1200 },
  };
}

const signature = { strokesJson: '{"strokes":[[[1,2,0.5],[30,40,0.5]]]}', png: new Uint8Array([137, 80, 78, 71, 1, 2, 3]) };
const context: SigningContext = {
  userId: "0192f0c1-0000-7000-8000-000000000501", appVersion: "1.4.0", now: () => new Date("2026-11-09T06:03:00.000Z"),
  position: () => Promise.resolve({ lat: 36.804512, lon: 10.0056, acc: 13 }),
};
const asset = "0192f0c1-0000-7000-8000-000000000a01";
const meter = "0192f0c1-0000-7000-8000-000000000b01";

beforeEach(async () => {
  db = openOfflineDb(`sign-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  publicKey = keys.publicKey;
  await db.device.put({
    id: "this", deviceId: "0192f0c1-0000-7000-8000-000000000d01", tenantId: "t1", siteId: "s1", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32)), enrolledAt: "2026-11-09T05:00:00Z", clockOffsetMs: 0,
  });
});

afterEach(async () => { await db.delete(); });

interface Payload {
  record: Record<string, unknown>;
  envelope: SignatureEnvelopeV1;
  deviceSignature: string;
  captures: Photo["capture"][];
  evidence: { sha256: string; size: number; contentType: string }[];
}

describe("signing a shift entry (P2.5, ADR-0033, ADR-0034 §5)", () => {
  it("signs a start: the envelope binds the record, the meter photo and the handwriting, signed by the tablet for the operator", async () => {
    const meterPhoto = await photo(7);

    const command = await signStart(db, context, {
      assetId: asset, meterId: meter, startedAt: new Date("2026-11-09T06:02:00Z"), startValue: "12482.5", fuelStartPercent: 75, photo: meterPhoto, signature, remarks: "froid",
    });

    const { record, envelope, deviceSignature, captures, evidence } = command.payload as Payload;
    expect(command.type).toBe(startCommand);
    expect(record).toEqual({
      rootId: envelope.recordId, version: 1, assetId: asset, meterId: meter, startedAt: "2026-11-09T06:02:00.000Z", startValue: "12482.5", fuelStartPercent: 75,
      startPhoto: `sha256:${meterPhoto.capture.sha256}`, remarks: "froid",
    });
    expect(envelope).toMatchObject({
      v: 1, recordType: "fieldops.shift_entry", recordVersion: 1, payloadHash: await payloadHash(record),
      evidence: [`sha256:${meterPhoto.capture.sha256}`], handwritten: await handwrittenHashes(signature.strokesJson, signature.png),
      signer: { userId: context.userId, role: "operator", deviceId: "0192f0c1-0000-7000-8000-000000000d01" },
      context: { deviceTime: "2026-11-09T06:03:00.000Z", appVersion: "1.4.0", geo: { lat: 36.804512, lon: 10.0056, acc: 13 } },
    });
    expect(await verifyEnvelope(publicKey, envelope, deviceSignature)).toBe(true);
    expect(captures).toEqual([meterPhoto.capture]);
    expect(evidence).toEqual([
      { sha256: meterPhoto.capture.sha256, size: 64, contentType: "image/webp" },
      { sha256: await sha256Hex(utf8(signature.strokesJson)), size: signature.strokesJson.length, contentType: "application/json" },
      { sha256: await sha256Hex(signature.png), size: 7, contentType: "image/png" },
    ]);
    expect(await db.blobs.count()).toBe(3);
  });

  it("changing the signed record breaks the envelope (a changed value cannot pass for the signed one)", async () => {
    const command = await signStart(db, context, { assetId: asset, meterId: meter, startedAt: new Date("2026-11-09T06:02:00Z"), startValue: "10.0", fuelStartPercent: 50, photo: await photo(1), signature });
    const { record, envelope } = command.payload as Payload;

    expect(envelope.payloadHash).not.toBe(await payloadHash({ ...record, startValue: "11.0" }));
  });

  it("signs an end as the next version, without stoppages when there were none, and without a position when there is none", async () => {
    const endPhoto = await photo(9);
    const command = await signEnd(db, { ...context, position: () => Promise.resolve(null) }, {
      rootId: "0192f0c1-0000-7000-8000-0000000e0001", version: 2, endedAt: new Date("2026-11-09T14:00:00Z"), endValue: "12490.0", fuelEndPercent: 40,
      photo: endPhoto, signature, stoppages: [],
    });

    const { record, envelope } = command.payload as Payload;
    expect(command.type).toBe(endCommand);
    expect(record).toEqual({
      rootId: "0192f0c1-0000-7000-8000-0000000e0001", version: 2, endedAt: "2026-11-09T14:00:00.000Z", endValue: "12490.0", fuelEndPercent: 40,
      endPhoto: `sha256:${endPhoto.capture.sha256}`,
    });
    expect(envelope.recordVersion).toBe(2);
    expect(envelope.context.geo).toBeUndefined();
  });

  it("never signs a record the server would refuse, and records nothing", async () => {
    const attempt = signStart(db, context, { assetId: asset, meterId: meter, startedAt: new Date("2026-11-09T06:02:00Z"), startValue: "12.34", fuelStartPercent: 75, photo: await photo(2), signature });

    await expect(attempt).rejects.toEqual(new RecordRefused("startValue"));
    expect(await db.outbox.count()).toBe(0);
    expect(await db.blobs.count()).toBe(0);
  });
});

describe("the position for the envelope", () => {
  it("is rounded as the server stores it, and null when refused or without geolocation", async () => {
    const granted = { getCurrentPosition: (ok: PositionCallback) => { ok({ coords: { latitude: 36.80451234, longitude: 10.00559999, accuracy: 12.2 } } as GeolocationPosition); } } as Geolocation;
    const refused = { getCurrentPosition: (_: PositionCallback, fail?: PositionErrorCallback | null) => { fail?.({ code: 1 } as GeolocationPositionError); } } as Geolocation;

    expect(await browserPosition(granted)()).toEqual({ lat: 36.804512, lon: 10.0056, acc: 13 });
    expect(await browserPosition(refused)()).toBeNull();
    expect(await browserPosition(undefined)()).toBeNull();
  });
});
