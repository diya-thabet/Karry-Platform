// WebCrypto device keys, device-signed requests, RFC 8785 and the offline PIN check (ADR-0028).
// Source-first internal package: apps import TypeScript directly, no build step.
export { canonicalize } from "./canonicalize";
export { assertionLifetime, deviceAssertion, enrollmentProof, generateDeviceKey, publicJwk, sign, verify, type DevicePublicKey, type DeviceRequest } from "./device";
export { fromBase64, fromBase64Url, toBase64, toBase64Url, utf8 } from "./encoding";
export { checkPin, importPinKey, pinVerifier, type PinVerifier } from "./pin";
export {
  envelopeProblem, maxEnvelopeEvidence, payloadHash, sha256Text, signEnvelope, verifyEnvelope, type ContentHash, type SignatureEnvelopeV1, type SignedEnvelope,
} from "./envelope";
