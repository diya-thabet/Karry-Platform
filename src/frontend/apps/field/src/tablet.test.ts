import "fake-indexeddb/auto";
import { fromBase64Url, toBase64, utf8 } from "@karry/crypto";
import { openOfflineDb } from "@karry/offline";
import { codeOf, companyOf, enrollPath, enrollTablet } from "./tablet";

const info = { model: "SM-X110", platform: "Android 14", appVersion: "1.2.3" };
const input = { company: " Demo-Carriere-A ", code: " K7QM-2XRA-9D4T-WB3N ", name: " Pont-bascule entrée " };

function only<T>(value: T | undefined): T {
  if (value === undefined) {
    throw new Error("missing");
  }

  return value;
}

function answer(status: number, body: unknown): typeof fetch {
  return vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })));
}

const enrolled = {
  deviceId: "0192f0a1-0000-7000-8000-000000000001", tenantId: "0192f0a1-0000-7000-8000-000000000002", siteId: "0192f0a1-0000-7000-8000-000000000003",
  pinKey: toBase64(new Uint8Array(32).fill(7)), settings: { autoLockMinutes: 5, offlineSessionHours: 72 }, serverTime: "2026-10-28T10:00:00Z",
};

describe("enrolling the tablet (US-P1-OFF-02 AC1, ADR-0028 §2)", () => {
  it("sends a new public key with proof of its private key, then keeps the device, its keys and settings", async () => {
    const db = openOfflineDb(`enroll-${crypto.randomUUID()}`);
    const send = answer(201, enrolled);

    const device = await enrollTablet(db, input, info, send);

    const [path, init] = only(vi.mocked(send).mock.calls[0]);
    const body = JSON.parse(only(init).body as string) as Record<string, unknown> & { publicKey: JsonWebKey; proof: string };
    expect(path).toBe(enrollPath);
    expect(body).toMatchObject({ tenant: "demo-carriere-a", code: "K7QM-2XRA-9D4T-WB3N", name: "Pont-bascule entrée", ...info });
    const publicKey = await crypto.subtle.importKey("jwk", body.publicKey, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    expect(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, fromBase64Url(body.proof) as Uint8Array<ArrayBuffer>,
      utf8("karry-enroll:K7QM-2XRA-9D4T-WB3N"))).toBe(true);

    const stored = only(await db.device.get("this"));
    expect(stored).toMatchObject({ deviceId: enrolled.deviceId, tenantId: enrolled.tenantId, siteId: enrolled.siteId, publicKey: body.publicKey, enrolledAt: enrolled.serverTime });
    expect(stored).toEqual(device);
    expect(stored.privateKey.extractable).toBe(false);
    expect(stored.pinKey.extractable).toBe(false);
    expect(Math.abs(stored.clockOffsetMs - (Date.parse(enrolled.serverTime) - Date.now()))).toBeLessThan(5_000);
    expect(await db.settings.get("this")).toEqual({ id: "this", autoLockMinutes: 5, offlineSessionHours: 72 });
  });

  it.each([
    [422, { code: "devices.enrollment_expired" }, "expired"],
    [404, { code: "devices.enrollment_unknown" }, "unknown"],
    [400, { code: "request.invalid" }, "invalid"],
    [500, "not json", "failed"],
  ] as const)("answers %i %j as %s and keeps nothing (TC-P1-OFF-002)", async (status, body, failure) => {
    const db = openOfflineDb(`enroll-${crypto.randomUUID()}`);

    expect(await enrollTablet(db, input, info, answer(status, body))).toBe(failure);

    expect(await db.device.count()).toBe(0);
    expect(await db.settings.count()).toBe(0);
  });

  it("reports no connection when the request cannot be sent", async () => {
    const db = openOfflineDb(`enroll-${crypto.randomUUID()}`);

    expect(await enrollTablet(db, input, info, vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))))).toBe("offline");
    expect(await db.device.count()).toBe(0);
  });
});

describe("the tablet's address", () => {
  it.each([
    ["demo-carriere-a.localhost", "demo-carriere-a"],
    ["sablieres-sud.field.karry.app", "sablieres-sud"],
    ["localhost", null],
    ["127.0.0.1", null],
  ])("%s names the company %s", (hostname, company) => {
    expect(companyOf(hostname)).toBe(company);
  });

  it("reads the code of the portal's enrollment link and nothing else", () => {
    expect(codeOf({ pathname: "/enroll", hash: "#K7QM-2XRA-9D4T-WB3N" })).toBe("K7QM-2XRA-9D4T-WB3N");
    expect(codeOf({ pathname: "/enroll", hash: "" })).toBeNull();
    expect(codeOf({ pathname: "/", hash: "#K7QM" })).toBeNull();
  });
});
