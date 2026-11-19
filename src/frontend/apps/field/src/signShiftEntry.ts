import { handwrittenHashes, payloadHash, signEnvelope, utf8, type SignatureEnvelopeV1 } from "@karry/crypto";
import { enqueue, uuidv7, type ChecklistTemplateRow, type KarryOfflineDb, type OutboxRecord } from "@karry/offline";
import type { Photo } from "./camera";
import { photoReference } from "./camera";
import { checklistCommand, checklistRecordType, criticalNotOk, type Answer, type AnswerRecord, type ChecklistRecord } from "./checklistRules";
import { endCommand, startCommand } from "./meterState";
import { endRecordProblem, recordType, startRecordProblem, type EndRecord, type StartRecord, type Stoppage } from "./shiftRules";

/** A handwritten signature as the pad hands it over (`SignatureCapture` of `@karry/ui`). */
export interface Handwriting {
  strokesJson: string;
  png: Uint8Array;
}

/** Where the tablet was, as the envelope carries it (ADR-0033 `context.geo`). */
export interface Position {
  lat: number;
  lon: number;
  acc: number;
}

export interface SigningContext {
  /** The unlocked operator: the envelope's signer (ADR-0034: the operator is never a member of the record). */
  userId: string;
  appVersion: string;
  now?: () => Date;
  /** The position for the envelope, null when the browser gives none in time; never a blocker. */
  position?: () => Promise<Position | null>;
}

export interface StartInput {
  rootId?: string;
  assetId: string;
  meterId: string;
  startedAt: Date;
  /** As the record writes it ("12482.5"). */
  startValue: string;
  fuelStartPercent: number;
  photo: Photo;
  signature: Handwriting;
  remarks?: string;
  previousRootId?: string;
}

export interface EndInput {
  rootId: string;
  version: number;
  endedAt: Date;
  endValue: string;
  fuelEndPercent: number;
  photo: Photo;
  signature: Handwriting;
  stoppages?: Stoppage[];
  remarks?: string;
}

/** A record the server would refuse (`fieldops.entry_invalid`): it is never signed. `member` is the first one at fault. */
export class RecordRefused extends Error {
  constructor(public readonly member: string) {
    super(`The record is not valid (${member}).`);
    this.name = "RecordRefused";
  }
}

/** How long the tablet waits for a position before signing without one. */
export const positionTimeoutMs = 10_000;

/** The browser's position, rounded as the server stores it, or null (refused, unavailable, or too slow). */
export function browserPosition(geolocation: Geolocation | undefined = typeof navigator === "undefined" ? undefined : navigator.geolocation): () => Promise<Position | null> {
  return () => new Promise((resolve) => {
    if (!geolocation) {
      resolve(null);
      return;
    }

    geolocation.getCurrentPosition(
      (p) => { resolve({ lat: round(p.coords.latitude, 6), lon: round(p.coords.longitude, 6), acc: Math.ceil(p.coords.accuracy) }); },
      () => { resolve(null); },
      { enableHighAccuracy: true, timeout: positionTimeoutMs, maximumAge: 2 * 60 * 1000 },
    );
  });
}

const round = (value: number, decimals: number) => Math.round(value * 10 ** decimals) / 10 ** decimals;

/**
 * Signs the start of a shift entry (P2.5, ADR-0034 §5) and puts it in the outbox: the record (checked with the rules the
 * server applies, so a signed fact is never refused for its shape), an envelope binding it with the meter photo and the
 * handwritten signature (ADR-0033), signed with the device key, and the evidence: the photo, the strokes and their PNG.
 * The photo's capture record rides in the signed command payload (`captures`, ADR-0032 §5).
 * So does the clock offset the tablet knew when signing (`clock`, server − tablet), for the clock skew rule (RULE-FO-03).
 */
export async function signStart(db: KarryOfflineDb, context: SigningContext, input: StartInput): Promise<OutboxRecord> {
  const record: StartRecord = {
    rootId: input.rootId ?? uuidv7(), version: 1, assetId: input.assetId, meterId: input.meterId, startedAt: input.startedAt.toISOString(),
    startValue: input.startValue, fuelStartPercent: input.fuelStartPercent, startPhoto: photoReference(input.photo.capture.sha256),
    ...(input.previousRootId === undefined ? {} : { previousRootId: input.previousRootId }),
    ...(input.remarks === undefined ? {} : { remarks: input.remarks }),
  };
  const problem = startRecordProblem(record);
  if (problem !== null) {
    throw new RecordRefused(problem);
  }

  return signAndEnqueue(db, context, startCommand, record, 1, input.photo, input.signature);
}

/** Signs the end of a shift entry (its next version) and puts it in the outbox, as {@link signStart} does. */
export async function signEnd(db: KarryOfflineDb, context: SigningContext, input: EndInput): Promise<OutboxRecord> {
  const record: EndRecord = {
    rootId: input.rootId, version: input.version, endedAt: input.endedAt.toISOString(), endValue: input.endValue,
    fuelEndPercent: input.fuelEndPercent, endPhoto: photoReference(input.photo.capture.sha256),
    ...(input.stoppages === undefined || input.stoppages.length === 0 ? {} : { stoppages: input.stoppages }),
    ...(input.remarks === undefined ? {} : { remarks: input.remarks }),
  };
  const problem = endRecordProblem(record);
  if (problem !== null) {
    throw new RecordRefused(problem);
  }

  return signAndEnqueue(db, context, endCommand, record, input.version, input.photo, input.signature);
}

async function signAndEnqueue(
  db: KarryOfflineDb, context: SigningContext, type: string, record: StartRecord | EndRecord, version: number, photo: Photo, signature: Handwriting,
): Promise<OutboxRecord> {
  const device = await db.device.get("this");
  if (!device) {
    throw new Error("The tablet is not enrolled.");
  }

  const png = signature.png as Uint8Array<ArrayBuffer>;
  const geo = await (context.position ?? (() => Promise.resolve(null)))();
  const envelope: SignatureEnvelopeV1 = {
    v: 1,
    envelopeId: uuidv7(),
    recordType,
    recordId: record.rootId,
    recordVersion: version,
    payloadHash: await payloadHash(record),
    evidence: [photoReference(photo.capture.sha256) as `sha256:${string}`],
    handwritten: await handwrittenHashes(signature.strokesJson, png),
    signer: { userId: context.userId, role: "operator", deviceId: device.deviceId },
    context: { deviceTime: (context.now ?? (() => new Date()))().toISOString(), appVersion: context.appVersion, ...(geo === null ? {} : { geo }) },
  };
  const signed = await signEnvelope(device.privateKey, envelope);
  return enqueue(db, {
    type,
    payload: { record, envelope: signed.envelope, deviceSignature: signed.deviceSignature, captures: [photo.capture], clock: { offsetMs: Math.round(device.clockOffsetMs) } },
    evidence: [
      { content: photo.content, contentType: photo.contentType, thumbnail: photo.thumbnail },
      { content: utf8(signature.strokesJson), contentType: "application/json" },
      { content: png, contentType: "image/png" },
    ],
  });
}

export interface ChecklistInput {
  rootId?: string;
  template: ChecklistTemplateRow;
  assetId: string;
  submittedAt: Date;
  answers: { item: string; answer: Answer; photo?: Photo }[];
}

/**
 * Signs a pre-start checklist (P2.6, ADR-0035 §3) and puts it in the outbox: the record, checked with the server's
 * answer rules, and an envelope naming it with the photos of the Not OK answers as evidence, signed with the device key
 * for the unlocked operator (no handwritten signature: the operator signs the shift itself right after). Returns the
 * critical items answered Not OK: the machine is held from now on (ADR-0035 §4).
 */
export async function signChecklist(db: KarryOfflineDb, context: SigningContext, input: ChecklistInput): Promise<{ command: OutboxRecord; critical: string[] }> {
  const photos = input.answers.flatMap((a) => (a.photo ? [a.photo] : []));
  const record: ChecklistRecord = {
    rootId: input.rootId ?? uuidv7(), version: 1, templateId: input.template.id, templateVersion: input.template.version, assetId: input.assetId,
    submittedAt: input.submittedAt.toISOString(),
    answers: input.answers.map((a): AnswerRecord => (a.photo ? { item: a.item, answer: a.answer, photo: photoReference(a.photo.capture.sha256) } : { item: a.item, answer: a.answer })),
  };
  const critical = criticalNotOk(input.template.items, record.answers);
  if (critical === null) {
    throw new RecordRefused("answers");
  }

  const device = await db.device.get("this");
  if (!device) {
    throw new Error("The tablet is not enrolled.");
  }

  const geo = await (context.position ?? (() => Promise.resolve(null)))();
  const envelope: SignatureEnvelopeV1 = {
    v: 1,
    envelopeId: uuidv7(),
    recordType: checklistRecordType,
    recordId: record.rootId,
    recordVersion: 1,
    payloadHash: await payloadHash(record),
    ...(photos.length === 0 ? {} : { evidence: photos.map((photo) => photoReference(photo.capture.sha256) as `sha256:${string}`) }),
    signer: { userId: context.userId, role: "operator", deviceId: device.deviceId },
    context: { deviceTime: (context.now ?? (() => new Date()))().toISOString(), appVersion: context.appVersion, ...(geo === null ? {} : { geo }) },
  };
  const signed = await signEnvelope(device.privateKey, envelope);
  const command = await enqueue(db, {
    type: checklistCommand,
    payload: {
      record, envelope: signed.envelope, deviceSignature: signed.deviceSignature,
      ...(photos.length === 0 ? {} : { captures: photos.map((photo) => photo.capture) }),
    },
    evidence: photos.map((photo) => ({ content: photo.content, contentType: photo.contentType, thumbnail: photo.thumbnail })),
  });
  return { command, critical };
}
