import "fake-indexeddb/auto";
import { generateDeviceKey, importPinKey, pinVerifier, publicJwk, toBase64 } from "@karry/crypto";
import { openOfflineDb, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";
import { failuresBeforeLock, lockMs, unlock, unlockPath } from "./unlock";

const operator = { id: "0192f0a1-0000-7000-8000-0000000000e1", employeeId: "DEMO-OPERATOR", name: "Opérateur Démo" };
const pin = "804613";
const hour = 60 * 60 * 1000;

/** An enrolled tablet whose reference data knows the operator's PIN verifier, and a server answering as prepared. */
async function setup() {
  const db = openOfflineDb(`unlock-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  const pinKey = await importPinKey(new Uint8Array(32).fill(3));
  const device: DeviceRecord = {
    id: "this", deviceId: "0192f0a1-0000-7000-8000-0000000000d1", tenantId: "0192f0a1-0000-7000-8000-0000000000a1",
    siteId: "0192f0a1-0000-7000-8000-000000000051", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey), pinKey,
    enrolledAt: "2026-10-28T10:00:00Z", clockOffsetMs: 0,
  };
  await db.device.put(device);
  await db.settings.put({ id: "this", autoLockMinutes: 5, offlineSessionHours: 72 });
  const salt = toBase64(new Uint8Array(16).fill(9));
  await db.users.put({ ...operator, pin: { salt, iterations: 1000, verifier: await pinVerifier(pinKey, pin, salt, 1000) } });

  const bodies: unknown[] = [];
  const answers: (Response | Error)[] = [];
  const send = vi.fn((_: string | URL | Request, init?: RequestInit) => {
    bodies.push(JSON.parse(init?.body as string));
    const next = answers.shift() ?? new TypeError("Failed to fetch");
    return next instanceof Error ? Promise.reject(next) : Promise.resolve(next);
  }) as unknown as typeof fetch;
  const reply = (status: number, body: unknown) => answers.push(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
  return { db, send, bodies, reply };
}

/** The operator unlocked online on this tablet that long ago (server time). */
async function onlineAgo(db: KarryOfflineDb, ms: number, failures = 0) {
  await db.unlocks.put({ userId: operator.id, lastOnlineAt: new Date(Date.now() - ms).toISOString(), failures, lockedUntil: null });
}

const offline = vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))) as unknown as typeof fetch;

describe("unlocking the shared tablet online (US-P1-OFF-04 AC1, ADR-0028 §4)", () => {
  it("lets the server check the PIN with a signed request, then notes the time for offline unlocks", async () => {
    const { db, send, bodies, reply } = await setup();
    reply(200, { userId: operator.id, name: operator.name, serverTime: "2026-10-28T12:00:00+00:00" });

    expect(await unlock(db, " DEMO-OPERATOR ", pin, send)).toEqual({ userId: operator.id, name: operator.name, employeeId: "DEMO-OPERATOR", checkedBy: "server" });

    const [url, init] = vi.mocked(send).mock.calls[0] ?? [];
    expect(url).toBe(unlockPath);
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).get("Authorization")).toMatch(/^Device /);
    expect(bodies).toEqual([{ employeeId: "DEMO-OPERATOR", pin }]);
    expect(await db.unlocks.get(operator.id)).toEqual({ userId: operator.id, lastOnlineAt: "2026-10-28T12:00:00+00:00", failures: 0, lockedUntil: null });
  });

  it.each([
    [401, "identity.pin_wrong", "wrong"],
    [401, "identity.pin_locked", "locked"],
    [401, "identity.unlock_refused", "refused"],
    [401, "devices.revoked", "revoked"],
    [400, "request.invalid", "wrong"],
    [403, "forbidden", "failed"],
  ] as const)("answers %i %s as %s without trying the PIN on the tablet", async (status, code, failure) => {
    const { db, send, reply } = await setup();
    await onlineAgo(db, hour);
    reply(status, { code });

    expect(await unlock(db, "DEMO-OPERATOR", pin, send)).toBe(failure);
    expect((await db.unlocks.get(operator.id))?.failures).toBe(0);
  });

  it("checks the PIN on the tablet when the server fails", async () => {
    const { db, send, reply } = await setup();
    await onlineAgo(db, hour);
    reply(503, { code: "unavailable" });

    expect(await unlock(db, "DEMO-OPERATOR", pin, send)).toMatchObject({ userId: operator.id, checkedBy: "tablet" });
  });
});

describe("unlocking the shared tablet offline (US-P1-OFF-04 AC1/AC2, ADR-0028 §4)", () => {
  it("accepts the right PIN within 72 h of the last online unlock, in any case of the employee ID", async () => {
    const { db } = await setup();
    await onlineAgo(db, 71 * hour);

    expect(await unlock(db, "demo-operator", pin, offline)).toEqual({ userId: operator.id, name: operator.name, employeeId: "DEMO-OPERATOR", checkedBy: "tablet" });
  });

  it("needs the network for the first unlock on this tablet, after 72 h (TC-P1-OFF-003), and for someone it does not know", async () => {
    const { db } = await setup();
    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toBe("online");

    await onlineAgo(db, 73 * hour);
    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toBe("online");

    expect(await unlock(db, "DEMO-NOBODY", pin, offline)).toBe("online");
  });

  it("follows the offline window the server set", async () => {
    const { db } = await setup();
    await db.settings.put({ id: "this", autoLockMinutes: 5, offlineSessionHours: 8 });
    await onlineAgo(db, 9 * hour);

    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toBe("online");
  });

  it("locks the user for 5 minutes after 5 wrong PINs, even with the right one, then lets them try again", async () => {
    const { db } = await setup();
    await onlineAgo(db, hour);

    const answers = [];
    for (let i = 0; i < failuresBeforeLock; i++) {
      answers.push(await unlock(db, "DEMO-OPERATOR", "000000", offline));
    }

    expect(answers).toEqual(["wrong", "wrong", "wrong", "wrong", "locked"]);
    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toBe("locked");
    const lockedUntil = Date.parse((await db.unlocks.get(operator.id))?.lockedUntil ?? "");
    expect(Math.abs(lockedUntil - Date.now() - lockMs)).toBeLessThan(5_000);

    await db.unlocks.update(operator.id, { lockedUntil: new Date(Date.now() - 1).toISOString() });
    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toMatchObject({ checkedBy: "tablet" });
    expect(await db.unlocks.get(operator.id)).toMatchObject({ failures: 0, lockedUntil: null });
  });

  it("needs an online unlock after 10 wrong PINs, and an online unlock clears the count", async () => {
    const { db, send, reply } = await setup();
    await onlineAgo(db, hour, 9);

    expect(await unlock(db, "DEMO-OPERATOR", "000000", offline)).toBe("online");
    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toBe("online");

    reply(200, { userId: operator.id, name: operator.name, serverTime: new Date().toISOString() });
    await unlock(db, "DEMO-OPERATOR", pin, send);
    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toMatchObject({ checkedBy: "tablet" });
  });

  it("refuses a user whose verifier the last refresh removed (TC-P1-IAM-004)", async () => {
    const { db } = await setup();
    await onlineAgo(db, hour);
    await db.users.delete(operator.id);

    expect(await unlock(db, "DEMO-OPERATOR", pin, offline)).toBe("online");
  });
});
