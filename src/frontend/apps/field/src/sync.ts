import { deviceAssertion } from "@karry/crypto";
import { applyChanges, collections, type ChangeSet, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";

export const changesPath = "/api/v1/sync/changes";

/** A full snapshot at least this often, even when deltas succeed (ADR-0028 §5). */
export const snapshotEveryMs = 24 * 60 * 60 * 1000;

/** How a refresh of the reference data ended. */
export type PullOutcome = "ok" | "offline" | "revoked" | "failed";

/**
 * A request signed by the tablet (ADR-0028 §3), dated by the server's clock as far as the tablet knows it. When the
 * server says the clock is wrong it sends its time: the offset is corrected and kept, and the request is sent once more.
 */
export async function deviceFetch(db: KarryOfflineDb, device: DeviceRecord, method: string, url: string, send: typeof fetch = fetch): Promise<Response> {
  let offset = device.clockOffsetMs;
  for (let attempt = 0; ; attempt++) {
    const authorization = await deviceAssertion(device.privateKey, {
      deviceId: device.deviceId, tenantId: device.tenantId, method, path: url.split("?")[0] ?? url, now: Date.now() + offset,
    });
    const response = await send(url, { method, headers: { Authorization: `Device ${authorization}` } });
    const serverTime = attempt === 0 && response.status === 401 ? await clockSkew(response.clone()) : null;
    if (serverTime === null) {
      return response;
    }

    offset = Date.parse(serverTime) - Date.now();
    device.clockOffsetMs = offset;
    await db.device.update("this", { clockOffsetMs: offset });
  }
}

/**
 * Refreshes the reference data of the tablet's site (US-P1-OFF-03): a delta from the last cursor, or a full snapshot
 * when there is none, the last one is a day old, or the server no longer accepts the cursor. The answer is applied in
 * one IndexedDB transaction.
 */
export async function pullChanges(db: KarryOfflineDb, send: typeof fetch = fetch): Promise<PullOutcome> {
  const device = await db.device.get("this");
  if (!device) {
    return "failed";
  }

  try {
    let response = await deviceFetch(db, device, "GET", await changesUrl(db, Date.now() + device.clockOffsetMs), send);
    if (response.status === 422 || response.status === 400) {
      // sync.cursor_expired or sync.cursor_invalid: start again from a snapshot.
      response = await deviceFetch(db, device, "GET", changesPath, send);
    }

    if (response.status === 401 && (await problemCode(response.clone())) === "devices.revoked") {
      return "revoked";
    }

    if (!response.ok) {
      return "failed";
    }

    await applyChanges(db, (await response.json()) as ChangeSet);
    return "ok";
  } catch (error) {
    return error instanceof TypeError ? "offline" : "failed";
  }
}

/** The cursor to continue from, unless a snapshot is due. */
async function changesUrl(db: KarryOfflineDb, serverNow: number): Promise<string> {
  const states = await db.syncState.toArray();
  const cursor = states[0]?.cursor ?? null;
  const lastFull = states.map((s) => s.lastFullAt).filter((t): t is string => t !== null).sort()[0];
  const complete = states.length === collections.length && states.every((s) => s.cursor === cursor);
  const fresh = lastFull !== undefined && serverNow - Date.parse(lastFull) < snapshotEveryMs;
  return cursor !== null && complete && fresh ? `${changesPath}?cursor=${encodeURIComponent(cursor)}` : changesPath;
}

async function clockSkew(response: Response): Promise<string | null> {
  try {
    const problem = (await response.json()) as { code?: string; serverTime?: string };
    return problem.code === "devices.clock_skew" && typeof problem.serverTime === "string" ? problem.serverTime : null;
  } catch {
    return null;
  }
}

async function problemCode(response: Response): Promise<string | null> {
  try {
    return ((await response.json()) as { code?: string }).code ?? null;
  } catch {
    return null;
  }
}
