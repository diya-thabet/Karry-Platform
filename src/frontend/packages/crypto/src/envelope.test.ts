import { readFileSync } from "node:fs";
import { canonicalize, envelopeProblem, generateDeviceKey, handwrittenHashes, payloadHash, sha256Text, signEnvelope, verifyEnvelope, type SignatureEnvelopeV1 } from "./index";

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

  it("binds the signature pad's strokes text and PNG bytes into the signed envelope (TC-P2-SIG-008)", async () => {
    const [minimal] = vectors.valid;
    if (!minimal) {
      throw new Error("the vectors have a minimal envelope");
    }

    // Reference hashes from Python's hashlib: the text as uploaded, and the PNG signature bytes.
    const handwritten = await handwrittenHashes('{"v":1,"w":600,"h":200,"strokes":[]}', new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
    const envelope = { ...minimal.envelope, handwritten };
    const keys = await generateDeviceKey();
    const signed = await signEnvelope(keys.privateKey, envelope);

    expect(handwritten).toEqual({
      strokesHash: "sha256:a0b5ff0ce1fdcead124e6d1fbdf835f700a19361a45e21f4d8ece7f1e34bc3db",
      imageHash: "sha256:4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6",
    });
    expect(signed.canonical).toContain(`"handwritten":{"imageHash":"${handwritten.imageHash}","strokesHash":"${handwritten.strokesHash}"}`);
    expect(await verifyEnvelope(keys.publicKey, { ...envelope, handwritten: { ...handwritten, imageHash: handwritten.strokesHash } }, signed.deviceSignature)).toBe(false);
  });
});
