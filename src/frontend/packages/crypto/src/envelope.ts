import { canonicalize } from "./canonicalize";
import { sign, verify } from "./device";
import { fromBase64Url, toBase64Url, utf8 } from "./encoding";

/** A content hash as the envelope writes it: `sha256:` and 64 lowercase hex characters. */
export type ContentHash = `sha256:${string}`;

/**
 * What the tablet signs for a record (ADR-0033 §1, envelope v1). Optional members are left out, never `null`, and no
 * other member is allowed, so the canonical text is unambiguous; a new member means envelope v2.
 */
export interface SignatureEnvelopeV1 {
  v: 1;
  envelopeId: string;
  recordType: string;
  recordId: string;
  recordVersion: number;
  payloadHash: ContentHash;
  evidence?: ContentHash[];
  handwritten?: { strokesHash: ContentHash; imageHash: ContentHash };
  signer: { userId: string; role: string; deviceId: string };
  context: { deviceTime: string; appVersion: string; geo?: { lat: number; lon: number; acc: number } };
}

/** A signed envelope: its canonical text, that text's hash, and the device's ES256 signature over it (base64url). */
export interface SignedEnvelope {
  envelope: SignatureEnvelopeV1;
  canonical: string;
  envelopeHash: ContentHash;
  deviceSignature: string;
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const hash = /^sha256:[0-9a-f]{64}$/;
const recordType = /^[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*$/;
const deviceTime = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/;
export const maxEnvelopeEvidence = 20;

type Members = Record<string, unknown>;

const isObject = (value: unknown): value is Members => typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown, max: number) => typeof value === "string" && value.length > 0 && value.length <= max;
const inRange = (value: unknown, min: number, max: number) => typeof value === "number" && Number.isFinite(value) && value >= min && value <= max;

/** The members of `value` are exactly `required` plus any of `optional`. */
function shape(value: unknown, required: string[], optional: string[] = []): value is Members {
  if (!isObject(value)) {
    return false;
  }

  const keys = Object.keys(value);
  return required.every((k) => keys.includes(k)) && keys.every((k) => required.includes(k) || optional.includes(k));
}

/**
 * Why `value` is not a valid envelope v1, or `null` when it is. The server applies the same rules
 * (`SignatureEnvelopeV1.Problem`), and both pass `tests/vectors/signature-envelope.json`, so the tablet never signs an
 * envelope the server would refuse.
 */
export function envelopeProblem(value: unknown): string | null {
  if (!shape(value, ["v", "envelopeId", "recordType", "recordId", "recordVersion", "payloadHash", "signer", "context"], ["evidence", "handwritten"])) {
    return "members";
  }

  if (value.v !== 1) {
    return "v";
  }

  if (typeof value.envelopeId !== "string" || !uuid.test(value.envelopeId) || typeof value.recordId !== "string" || !uuid.test(value.recordId)) {
    return "id";
  }

  if (typeof value.recordType !== "string" || value.recordType.length > 100 || !recordType.test(value.recordType)) {
    return "recordType";
  }

  if (typeof value.recordVersion !== "number" || !Number.isSafeInteger(value.recordVersion) || value.recordVersion < 1) {
    return "recordVersion";
  }

  if (typeof value.payloadHash !== "string" || !hash.test(value.payloadHash)) {
    return "payloadHash";
  }

  if ("evidence" in value) {
    const evidence = value.evidence;
    if (!Array.isArray(evidence) || evidence.length === 0 || evidence.length > maxEnvelopeEvidence
      || !evidence.every((h) => typeof h === "string" && hash.test(h)) || new Set(evidence).size !== evidence.length) {
      return "evidence";
    }
  }

  if ("handwritten" in value) {
    const handwritten = value.handwritten;
    if (!shape(handwritten, ["strokesHash", "imageHash"]) || ![handwritten.strokesHash, handwritten.imageHash].every((h) => typeof h === "string" && hash.test(h))) {
      return "handwritten";
    }
  }

  const signer = value.signer;
  if (!shape(signer, ["userId", "role", "deviceId"]) || typeof signer.userId !== "string" || !uuid.test(signer.userId)
    || typeof signer.deviceId !== "string" || !uuid.test(signer.deviceId) || !text(signer.role, 50)) {
    return "signer";
  }

  const context = value.context;
  if (!shape(context, ["deviceTime", "appVersion"], ["geo"]) || typeof context.deviceTime !== "string" || !deviceTime.test(context.deviceTime)
    || !text(context.appVersion, 40)) {
    return "context";
  }

  if ("geo" in context) {
    const geo = context.geo;
    if (!shape(geo, ["lat", "lon", "acc"]) || !inRange(geo.lat, -90, 90) || !inRange(geo.lon, -180, 180) || !inRange(geo.acc, 0, 100_000)) {
      return "geo";
    }
  }

  return null;
}

/** `sha256:` + hex of SHA-256 over UTF-8 text. */
export async function sha256Text(value: string): Promise<ContentHash> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", utf8(value)));
  return `sha256:${Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("")}`;
}

/** The hash of a record's payload that the envelope signs: SHA-256 over its RFC 8785 form. */
export const payloadHash = (payload: unknown): Promise<ContentHash> => sha256Text(canonicalize(payload));

/** Checks the envelope, then signs its canonical text with the device key (ES256, IEEE P1363). */
export async function signEnvelope(privateKey: CryptoKey, envelope: SignatureEnvelopeV1): Promise<SignedEnvelope> {
  const problem = envelopeProblem(envelope);
  if (problem) {
    throw new TypeError(`Not a valid signature envelope (${problem}).`);
  }

  const canonical = canonicalize(envelope);
  return { envelope, canonical, envelopeHash: await sha256Text(canonical), deviceSignature: toBase64Url(await sign(privateKey, canonical)) };
}

/** Checks a device signature over an envelope (what the server does on receipt). */
export async function verifyEnvelope(publicKey: CryptoKey, envelope: SignatureEnvelopeV1, deviceSignature: string): Promise<boolean> {
  return envelopeProblem(envelope) === null
    && verify(publicKey, canonicalize(envelope), fromBase64Url(deviceSignature) as Uint8Array<ArrayBuffer>);
}
