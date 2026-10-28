import { checkPin } from "@karry/crypto";
import { userByEmployeeId, type KarryOfflineDb, type UnlockRecord } from "@karry/offline";
import { deviceFetch } from "./sync";

export const unlockPath = "/api/v1/identity/devices/unlock";

/** Wrong PINs in a row before a user is locked out on this tablet, and for how long (ADR-0028 §4). */
export const failuresBeforeLock = 5;
export const lockMs = 5 * 60 * 1000;
/** Wrong PINs in a row after which only an online unlock is accepted. */
export const failuresBeforeOnline = 10;

/** Who has the tablet in hand; kept in memory only, gone when the tablet locks or the app closes. */
export interface Session {
  userId: string;
  name: string;
  employeeId: string;
  /** Whether the server checked the PIN, or the tablet did on its own. */
  checkedBy: "server" | "tablet";
}

/**
 * Why the tablet stays locked: a wrong PIN; too many wrong PINs for now; a check that needs the network (first unlock on
 * this tablet, 72 h without an online unlock, or 10 wrong PINs); a person who cannot unlock a tablet; a revoked tablet;
 * or a server that answered something unexpected.
 */
export type UnlockFailure = "wrong" | "locked" | "online" | "refused" | "revoked" | "failed";

/**
 * Unlocks the shared tablet for a field user (US-P1-OFF-04 AC1/AC2, ADR-0028 §4). Online, the server checks the PIN
 * against its hash and its own lockout, and the tablet notes the time. Without network (or with a failing server) the
 * tablet checks the PIN against the verifier of its reference data, if the user unlocked online on this tablet within
 * the offline window, with a lockout of its own: 5 wrong PINs lock for 5 minutes, 10 need an online unlock.
 */
export async function unlock(db: KarryOfflineDb, employeeId: string, pin: string, send: typeof fetch = fetch): Promise<Session | UnlockFailure> {
  const device = await db.device.get("this");
  if (!device) {
    return "failed";
  }

  const wanted = employeeId.trim();
  let response: Response;
  try {
    response = await deviceFetch(db, device, "POST", unlockPath, send, { employeeId: wanted, pin });
  } catch (error) {
    if (!(error instanceof TypeError)) {
      return "failed";
    }

    return unlockOffline(db, wanted, pin, Date.now() + device.clockOffsetMs);
  }

  if (response.status >= 500) {
    return unlockOffline(db, wanted, pin, Date.now() + device.clockOffsetMs);
  }

  if (response.ok) {
    const unlocked = (await response.json()) as { userId: string; name: string; serverTime: string };
    await db.unlocks.put({ userId: unlocked.userId, lastOnlineAt: unlocked.serverTime, failures: 0, lockedUntil: null });
    return { userId: unlocked.userId, name: unlocked.name, employeeId: wanted, checkedBy: "server" };
  }

  const code = await problemCode(response);
  switch (code) {
    case "identity.pin_wrong":
      return "wrong";
    case "identity.pin_locked":
      return "locked";
    case "identity.unlock_refused":
      return "refused";
    case "devices.revoked":
      return "revoked";
    default:
      // A refused request (400) only comes from an empty or overlong ID or PIN: for the person it is a wrong PIN.
      return response.status === 400 ? "wrong" : "failed";
  }
}

async function unlockOffline(db: KarryOfflineDb, employeeId: string, pin: string, serverNow: number): Promise<Session | UnlockFailure> {
  const user = await userByEmployeeId(db, employeeId);
  const device = await db.device.get("this");
  if (!user?.pin || !device) {
    // Unknown here, or without a PIN: a user created since the last refresh is known to the server only.
    return "online";
  }

  const record: UnlockRecord = (await db.unlocks.get(user.id)) ?? { userId: user.id, lastOnlineAt: null, failures: 0, lockedUntil: null };
  const hours = (await db.settings.get("this"))?.offlineSessionHours ?? 72;
  if (record.lastOnlineAt === null || serverNow - Date.parse(record.lastOnlineAt) > hours * 60 * 60 * 1000 || record.failures >= failuresBeforeOnline) {
    return "online";
  }

  if (record.lockedUntil !== null && serverNow < Date.parse(record.lockedUntil)) {
    return "locked";
  }

  if (await checkPin(device.pinKey, pin, user.pin)) {
    await db.unlocks.put({ ...record, failures: 0, lockedUntil: null });
    return { userId: user.id, name: user.name, employeeId: user.employeeId, checkedBy: "tablet" };
  }

  const failures = record.failures + 1;
  const locks = failures % failuresBeforeLock === 0;
  await db.unlocks.put({ ...record, failures, lockedUntil: locks ? new Date(serverNow + lockMs).toISOString() : record.lockedUntil });
  if (failures >= failuresBeforeOnline) {
    return "online";
  }

  return locks ? "locked" : "wrong";
}

async function problemCode(response: Response): Promise<string | null> {
  try {
    return ((await response.json()) as { code?: string }).code ?? null;
  } catch {
    return null;
  }
}
