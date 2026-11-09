import { liveQuery } from "dexie";
import { canonicalize, sign, toBase64Url } from "@karry/crypto";
import type { BlobRecord, KarryOfflineDb, OutboxRecord } from "./schema";

/** At most 1 MB per blob, so one pre-signed PUT and no multipart (ADR-0032 §3). */
export const maxBlobBytes = 1024 * 1024;

/** Photos, and signature strokes as JSON (ADR-0032 §2). */
export const blobContentTypes = ["image/jpeg", "image/webp", "image/png", "application/json"] as const;
export type BlobContentType = (typeof blobContentTypes)[number];

/** A batch the field app sends: at most 50 commands and about 2 MB (amendment A9; the server takes 100 × 64 KB). */
export const batchLimits = { commands: 50, bytes: 2 * 1024 * 1024 };

/** How long the bytes of a confirmed command's evidence stay on the tablet (ADR-0032 §6, a setting later). */
export const keepConfirmedBlobsMs = 7 * 24 * 60 * 60 * 1000;

/** A command as the server takes it (`POST /api/v1/sync/batches`). */
export interface UploadedCommand {
  commandId: string;
  sequence: number;
  type: string;
  payload: unknown;
  deviceTime: string;
  signature: string;
}

/** The server's answer for one command. */
export interface CommandReceipt {
  commandId: string;
  status: "accepted" | "conflict" | "duplicate" | "waiting" | "rejected";
  code: string | null;
  retryable: boolean;
  flags: string[];
}

/** A row of the `submissions` collection of the pull: the final status of a command this tablet sent (ADR-0031 §8). */
export interface SubmissionRow {
  id: string;
  sequence: number;
  status: "accepted" | "conflict" | "waiting" | "rejected";
  code: string | null;
  flags: string[];
  at: string;
}

/** Evidence for a command: the exact bytes (EXIF already stripped by the camera screen) and an optional thumbnail. */
export interface EvidenceInput {
  content: Uint8Array;
  contentType: BlobContentType;
  thumbnail?: Uint8Array;
}

export interface EnqueueInput {
  type: string;
  /** The command's own payload; `evidence` is added from the blobs. */
  payload: Record<string, unknown>;
  evidence?: EvidenceInput[];
}

/** The lowercase hex SHA-256 of the bytes: the blob's name in its key and in the signed payload (ADR-0032 §1). */
export async function sha256Hex(content: Uint8Array): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", content as Uint8Array<ArrayBuffer>));
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** A UUIDv7 (RFC 9562): 48 bits of milliseconds, then random bits, lowercase as the server requires. */
export function uuidv7(now: number = Date.now(), random: (bytes: Uint8Array<ArrayBuffer>) => Uint8Array = (b) => crypto.getRandomValues(b)): string {
  const bytes = random(new Uint8Array(16));
  let ms = now;
  for (let i = 5; i >= 0; i--) {
    bytes[i] = ms % 256;
    ms = Math.floor(ms / 256);
  }

  bytes[6] = 0x70 | ((bytes[6] ?? 0) & 0x0f);
  bytes[8] = 0x80 | ((bytes[8] ?? 0) & 0x3f);
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** The bytes the device key signs: RFC 8785 of the five members, as the server checks them (ADR-0028 §6). */
export function commandBody(command: Omit<UploadedCommand, "signature">): string {
  const { commandId, sequence, type, payload, deviceTime } = command;
  return canonicalize({ commandId, sequence, type, payload, deviceTime });
}

/**
 * Records a command: its blobs are hashed and named in the payload, the command takes the next sequence number and is
 * signed, then the command, its blobs and the new last number are written in one IndexedDB transaction. Signing is
 * asynchronous and cannot run inside that transaction, so the number is checked again inside it and the command is
 * signed anew if another tab took it meanwhile: two tabs never give one number twice (TC-P2-SYNC-014).
 */
export async function enqueue(db: KarryOfflineDb, input: EnqueueInput, now: () => number = Date.now): Promise<OutboxRecord> {
  const device = await db.device.get("this");
  if (!device) {
    throw new Error("The tablet is not enrolled.");
  }

  const blobs: BlobRecord[] = [];
  for (const item of input.evidence ?? []) {
    if (item.content.byteLength < 1 || item.content.byteLength > maxBlobBytes) {
      throw new RangeError(`Evidence must hold 1 to ${String(maxBlobBytes)} bytes.`);
    }

    if (!blobContentTypes.includes(item.contentType)) {
      throw new RangeError(`Evidence of type ${item.contentType} is not accepted.`);
    }

    blobs.push({
      sha256: await sha256Hex(item.content), content: item.content, size: item.content.byteLength, contentType: item.contentType,
      thumbnail: item.thumbnail ?? null, uploadedAt: null, purgeAfter: null,
    });
  }

  const distinct = [...new Map(blobs.map((b) => [b.sha256, b])).values()];
  const payload = distinct.length === 0 ? input.payload
    : { ...input.payload, evidence: distinct.map((b) => ({ sha256: b.sha256, size: b.size, contentType: b.contentType })) };
  const commandId = uuidv7(now());
  const deviceTime = new Date(now()).toISOString();

  for (;;) {
    const last = await lastSequence(db);
    const unsigned = { commandId, sequence: last + 1, type: input.type, payload, deviceTime };
    const signature = toBase64Url(await sign(device.privateKey, commandBody(unsigned)));
    const record: OutboxRecord = {
      ...unsigned, signature, status: "pending", attempts: 0, code: null, flags: [], answeredAt: null, blobs: distinct.map((b) => b.sha256),
    };
    const written = await db.transaction("rw", db.outbox, db.blobs, db.device, async () => {
      if ((await lastSequence(db)) !== last) {
        return false;
      }

      for (const blob of distinct) {
        const stored = await db.blobs.get(blob.sha256);
        // The same photo again: the stored bytes stay; it is needed again until this command is confirmed.
        await db.blobs.put(stored ? { ...stored, content: stored.content ?? blob.content, purgeAfter: null } : blob);
      }

      await db.outbox.add(record);
      await db.device.update("this", { lastSequence: record.sequence });
      return true;
    });
    if (written) {
      return record;
    }
  }
}

async function lastSequence(db: KarryOfflineDb): Promise<number> {
  const [device, newest] = await Promise.all([db.device.get("this"), db.outbox.orderBy("sequence").last()]);
  return Math.max(device?.lastSequence ?? 0, newest?.sequence ?? 0);
}

export interface Batch {
  commands: UploadedCommand[];
  /** Blobs of these commands still to upload (presign, then PUT), before the batch is sent. */
  blobs: BlobRecord[];
}

/**
 * The next commands to send, in sequence order: the pending ones, and those the server listed as missing
 * (`missingSequences`), within {@link batchLimits}. Rejected commands are never sent again.
 */
export async function nextBatch(db: KarryOfflineDb, missing: readonly number[] = [], limits = batchLimits): Promise<Batch> {
  const wanted = new Set(missing);
  const candidates = await db.outbox.orderBy("sequence")
    .filter((c) => c.status === "pending" || (wanted.has(c.sequence) && c.status !== "rejected"))
    .toArray();
  const commands: UploadedCommand[] = [];
  let bytes = 0;
  for (const c of candidates) {
    const size = JSON.stringify(wire(c)).length;
    if (commands.length >= limits.commands || (commands.length > 0 && bytes + size > limits.bytes)) {
      break;
    }

    commands.push(wire(c));
    bytes += size;
  }

  const hashes = new Set(candidates.filter((c) => commands.some((s) => s.commandId === c.commandId)).flatMap((c) => c.blobs));
  const blobs = (await db.blobs.bulkGet([...hashes])).filter((b): b is BlobRecord => b?.uploadedAt === null && b.content !== null);
  return { commands, blobs };
}

function wire(c: OutboxRecord): UploadedCommand {
  return { commandId: c.commandId, sequence: c.sequence, type: c.type, payload: c.payload, deviceTime: c.deviceTime, signature: c.signature };
}

/** The store holds this blob now (uploaded, or it answered that it already had it). */
export async function markUploaded(db: KarryOfflineDb, sha256: string, at: Date): Promise<void> {
  await db.blobs.update(sha256, { uploadedAt: at.toISOString() });
}

/**
 * Applies the server's answers: accepted, flagged and duplicate commands are confirmed; `waiting` ones stay with the
 * server; a retryable rejection goes back to pending (and its blob is uploaded again on `sync.blob_missing`); a final
 * one is kept as rejected with its evidence. The blobs of confirmed commands may go 7 days later (ADR-0032 §6).
 */
export async function applyReceipts(db: KarryOfflineDb, receipts: readonly CommandReceipt[], at: Date): Promise<void> {
  await db.transaction("rw", db.outbox, db.blobs, async () => {
    for (const receipt of receipts) {
      const command = await db.outbox.get(receipt.commandId);
      if (!command) {
        continue;
      }

      const answered = { answeredAt: at.toISOString(), code: receipt.code, flags: receipt.flags };
      if (receipt.status === "rejected") {
        await db.outbox.update(command.commandId, receipt.retryable
          ? { ...answered, status: "pending", attempts: command.attempts + 1 }
          : { ...answered, status: "rejected" });
        if (receipt.code === "sync.blob_missing") {
          await Promise.all(command.blobs.map((h) => db.blobs.update(h, { uploadedAt: null })));
        }
      } else {
        await db.outbox.update(command.commandId, { ...answered, status: receipt.status === "waiting" ? "waiting" : "confirmed" });
      }
    }

    await schedulePurge(db, at);
  });
}

/**
 * Applies the `submissions` the pull brings (ADR-0031 §8): the final status of commands answered `waiting` earlier,
 * and a correction for any answer lost on the way. `sync.type_unknown` is not final: the command is sent again after
 * the server is updated.
 */
export async function applySubmissions(db: KarryOfflineDb, rows: readonly SubmissionRow[], at: Date): Promise<void> {
  await db.transaction("rw", db.outbox, db.blobs, async () => {
    for (const row of rows) {
      const command = await db.outbox.get(row.id);
      if (!command || command.status === "confirmed" || (command.status === "rejected" && row.status === "rejected")) {
        continue;
      }

      const status = row.status === "waiting" ? "waiting"
        : row.status === "rejected" ? (row.code === "sync.type_unknown" ? "pending" : "rejected")
        : "confirmed";
      await db.outbox.update(command.commandId, { status, code: row.code, flags: row.flags, answeredAt: row.at });
    }

    await schedulePurge(db, at);
  });
}

/** Sets when each blob's bytes may go: 7 days after every command naming it is confirmed; never while one is not. */
async function schedulePurge(db: KarryOfflineDb, at: Date): Promise<void> {
  const unfinished = new Set((await db.outbox.where("status").anyOf("pending", "waiting", "rejected").toArray()).flatMap((c) => c.blobs));
  const purgeAfter = new Date(at.getTime() + keepConfirmedBlobsMs).toISOString();
  await db.blobs.toCollection().modify((blob) => {
    if (unfinished.has(blob.sha256)) {
      blob.purgeAfter = null;
    } else {
      blob.purgeAfter ??= purgeAfter;
    }
  });
}

/**
 * Frees the bytes of evidence whose commands were confirmed more than 7 days ago (TC-P2-SYNC-011: never before). A
 * blob with a thumbnail keeps it for the tablet's history; one without is removed.
 */
export async function purgeBlobs(db: KarryOfflineDb, at: Date): Promise<number> {
  return db.transaction("rw", db.blobs, async () => {
    const due = await db.blobs.where("purgeAfter").belowOrEqual(at.toISOString()).toArray();
    for (const blob of due) {
      if (blob.thumbnail) {
        await db.blobs.update(blob.sha256, { content: null });
      } else {
        await db.blobs.delete(blob.sha256);
      }
    }

    return due.filter((b) => b.content !== null).length;
  });
}

/** What the sync centre shows: commands still to send, held by the server, refused, and the last answer. */
export interface OutboxSummary {
  pending: number;
  waiting: number;
  rejected: OutboxRecord[];
  lastAnswerAt: string | null;
}

export async function outboxSummary(db: KarryOfflineDb): Promise<OutboxSummary> {
  const [pending, waiting, rejected, answered] = await Promise.all([
    db.outbox.where("status").equals("pending").count(),
    db.outbox.where("status").equals("waiting").count(),
    db.outbox.where("status").equals("rejected").sortBy("sequence"),
    db.outbox.filter((c) => c.answeredAt !== null).toArray(),
  ]);
  const lastAnswerAt = answered.reduce<string | null>((latest, c) => (latest === null || (c.answeredAt ?? "") > latest ? c.answeredAt : latest), null);
  return { pending, waiting, rejected, lastAnswerAt };
}

/** Calls `next` with the summary now and after every change of the outbox (also from another tab); returns the stop. */
export function watchOutbox(db: KarryOfflineDb, next: (summary: OutboxSummary) => void): () => void {
  const subscription = liveQuery(() => outboxSummary(db)).subscribe({ next, error: () => undefined });
  return () => { subscription.unsubscribe(); };
}
