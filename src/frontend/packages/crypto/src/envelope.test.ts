import { readFileSync } from "node:fs";
import { canonicalize, envelopeProblem, generateDeviceKey, payloadHash, sha256Text, signEnvelope, verifyEnvelope, type SignatureEnvelopeV1 } from "./index";

interface Vectors {
  devicePublicKey: JsonWebKey;
  payload: unknown;
  payloadCanonical: string;
  payloadHash: string;
  valid: { name: string; envelope: SignatureEnvelopeV1; canonical: string; envelopeHash: string; deviceSignature: string; tamperedCanonical: string }[];
  invalid: { name: string; envelope: unknown }[];
}

const vectors = JSON.parse(readFileSync(new URL("../../../../../tests/vectors/signature-envelope.json", import.meta.url), "utf8")) as Vectors;
const publicKey = () => crypto.subtle.importKey("jwk", { ...vectors.devicePublicKey, ext: true }, { name: "ECDSA", namedCurve: "P-256" }, true, ["verify"]);

describe("signature envelope v1 (ADR-0033 §1, shared vectors)", () => {
  it("hashes the record's payload as the independent implementation does", async () => {
    expect(canonicalize(vectors.payload)).toBe(vectors.payloadCanonical);
    expect(await payloadHash(vectors.payload)).toBe(vectors.payloadHash);
  });

  it.each(vectors.valid.map((v) => [v.name, v] as const))("reproduces the canonical text and hash, and verifies the signature: %s", async (_name, v) => {
    expect(envelopeProblem(v.envelope)).toBeNull();
    expect(canonicalize(v.envelope)).toBe(v.canonical);
    expect(await sha256Text(v.canonical)).toBe(v.envelopeHash);
    expect(await verifyEnvelope(await publicKey(), v.envelope, v.deviceSignature)).toBe(true);
  });

  it("refuses the signature once one character of the envelope changes (TC-P2-SIG-002)", async () => {
    const [minimal] = vectors.valid;
    if (!minimal) {
      throw new Error("the vectors have a minimal envelope");
    }

    const changed = JSON.parse(minimal.tamperedCanonical) as SignatureEnvelopeV1;

    expect(await verifyEnvelope(await publicKey(), changed, minimal.deviceSignature)).toBe(false);
  });

  it.each(vectors.invalid.map((v) => [v.name, v] as const))("refuses a malformed envelope before signing: %s", async (_name, v) => {
    expect(envelopeProblem(v.envelope)).not.toBeNull();
    const keys = await generateDeviceKey();
    await expect(signEnvelope(keys.privateKey, v.envelope as SignatureEnvelopeV1)).rejects.toThrow(TypeError);
  });

  it("signs an envelope that its own key verifies, over the canonical text", async () => {
    const keys = await generateDeviceKey();
    const [, full] = vectors.valid;
    if (!full) {
      throw new Error("the vectors have a full envelope");
    }

    const signed = await signEnvelope(keys.privateKey, full.envelope);

    expect([signed.canonical, signed.envelopeHash]).toEqual([full.canonical, full.envelopeHash]);
    expect(await verifyEnvelope(keys.publicKey, full.envelope, signed.deviceSignature)).toBe(true);
  });
});
