import { applyReceipts, markUploaded, nextBatch, purgeBlobs, type BlobRecord, type CommandReceipt, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";
import { deviceFetch, pullChanges, type PullOutcome } from "./sync";

export const batchesPath = "/api/v1/sync/batches";
export const presignPath = "/api/v1/sync/blobs/presign";

/** How an upload ended. `busy`: the server asked the tablet to wait (429, 503) or another upload of it ran at once (409). */
export type PushOutcome = "ok" | "offline" | "revoked" | "failed" | "busy";

/** Batches sent in one run at most: a long outbox after days offline goes up in several runs, so the screen stays responsive. */
export const maxBatchesPerRun = 20;

/** Runs `work` while no other tab of the app uploads: one upload at a time on a tablet (Web Locks API, TC-P2-SYNC-014). */
export type WithLock = <T>(work: () => Promise<T>) => Promise<T>;

export const outboxLock: WithLock = (work) =>
  "locks" in navigator ? navigator.locks.request("karry-outbox", work) : work();

interface BatchReceipt {
  results: CommandReceipt[];
  lastSequence: number;
  missingSequences: number[];
  serverTime: string;
}

interface BlobUpload {
  exists: boolean;
  url: string | null;
  headers: Record<string, string> | null;
}

/**
 * Sends what the outbox holds (US-P2-OFF-01): for each batch, the evidence first (a pre-signed `PUT` straight to the
 * store, skipped when it already holds the content; ADR-0032 §2), then the signed commands; the answers are applied
 * at once, and the numbers the server says are missing go in the next batch. Nothing leaves the outbox here: a
 * command is only marked by the server's answer, so a run cut off at any point loses nothing (TC-P2-SYNC-002).
 */
export async function pushOutbox(db: KarryOfflineDb, send: typeof fetch = fetch, withLock: WithLock = outboxLock): Promise<PushOutcome> {
  return withLock(async () => {
    const device = await db.device.get("this");
    if (!device) {
      return "failed";
    }

    let missing: number[] = [];
    try {
      for (let run = 0; run < maxBatchesPerRun; run++) {
        const batch = await nextBatch(db, missing);
        if (batch.commands.length === 0) {
          return "ok";
        }

        for (const blob of batch.blobs) {
          const uploaded = await uploadBlob(db, device, blob, send);
          if (uploaded !== "ok") {
            return uploaded;
          }
        }

        const response = await deviceFetch(db, device, "POST", batchesPath, send, { commands: batch.commands });
        const failure = await failureOf(response);
        if (failure) {
          return failure;
        }

        const receipt = (await response.json()) as BatchReceipt;
        await applyReceipts(db, receipt.results, new Date(receipt.serverTime));
        // Numbers the server waits for that this tablet no longer has as pending are sent again from the outbox.
        missing = receipt.missingSequences;
        if (receipt.results.every((r) => r.status === "rejected" && r.retryable) && missing.length === 0) {
          return "busy";
        }
      }

      return "ok";
    } catch (error) {
      return error instanceof TypeError ? "offline" : "failed";
    }
  });
}

/** One blob: ask for an upload URL, then `PUT` the exact bytes with the signed headers. */
async function uploadBlob(db: KarryOfflineDb, device: DeviceRecord, blob: BlobRecord, send: typeof fetch): Promise<PushOutcome> {
  const response = await deviceFetch(db, device, "POST", presignPath, send, { sha256: blob.sha256, size: blob.size, contentType: blob.contentType });
  const failure = await failureOf(response);
  if (failure) {
    return failure;
  }

  const upload = (await response.json()) as BlobUpload;
  if (!upload.exists) {
    if (!upload.url || !blob.content) {
      return "failed";
    }

    const put = await send(upload.url, { method: "PUT", headers: upload.headers ?? {}, body: blob.content as Uint8Array<ArrayBuffer> });
    if (put.status === 429 || put.status >= 500) {
      return "busy";
    }

    if (!put.ok) {
      return "failed";
    }
  }

  await markUploaded(db, blob.sha256, new Date());
  return "ok";
}

async function failureOf(response: Response): Promise<PushOutcome | null> {
  if (response.ok) {
    return null;
  }

  if (response.status === 401 || response.status === 403) {
    const code = await response.clone().json().then((p: { code?: string }) => p.code ?? null, () => null);
    return code === "devices.revoked" ? "revoked" : "failed";
  }

  return response.status === 429 || response.status === 503 || response.status === 409 ? "busy" : "failed";
}

/**
 * One exchange with the server: the outbox goes up first, then the reference data and the statuses of the commands
 * sent come down, and evidence confirmed more than 7 days ago is freed. The outcome is the download's, except that a
 * revoked tablet or a busy server on the upload is reported as such.
 */
export async function syncTablet(db: KarryOfflineDb, send: typeof fetch = fetch, withLock: WithLock = outboxLock): Promise<PullOutcome> {
  const pushed = await pushOutbox(db, send, withLock);
  if (pushed === "revoked") {
    return "revoked";
  }

  const pulled = await pullChanges(db, send);
  await purgeBlobs(db, new Date());
  return pulled === "ok" && pushed === "busy" ? "busy" : pulled;
}
