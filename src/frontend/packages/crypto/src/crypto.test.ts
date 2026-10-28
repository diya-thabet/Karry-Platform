import { readFileSync } from "node:fs";
import { canonicalize, checkPin, deviceAssertion, enrollmentProof, fromBase64, fromBase64Url, generateDeviceKey, importPinKey, pinVerifier, publicJwk, verify } from "./index";

const vectors = (name: string): unknown => JSON.parse(readFileSync(new URL(`../../../../../tests/vectors/${name}`, import.meta.url), "utf8"));

describe("RFC 8785 canonicalization: the same bytes as the server (ADR-0028 §6)", () => {
  const jcs = vectors("rfc8785.json") as {
    examples: { input: string; expected: string }[];
    numbers: { bits: string; expected: string }[];
    documents: { input: string; expected: string }[];
  };

  it.each(jcs.examples.map((v, i) => [i, v] as const))("RFC example %i", (_i, { input, expected }) => {
    expect(canonicalize(JSON.parse(input))).toBe(expected);
  });

  it("serializes every number vector (IEEE 754 bit patterns) as the RFC says", () => {
    for (const { bits, expected } of jcs.numbers) {
      const view = new DataView(new ArrayBuffer(8));
      view.setBigUint64(0, BigInt(`0x${bits}`));
      expect(canonicalize(view.getFloat64(0)), bits).toBe(expected);
    }
  });

  it("canonicalizes the 300 generated documents", () => {
    for (const { input, expected } of jcs.documents) {
      expect(canonicalize(JSON.parse(input))).toBe(expected);
    }
  });

  it("refuses what JSON cannot carry", () => {
    expect(() => canonicalize(Number.NaN)).toThrow(TypeError);
    expect(() => canonicalize(Number.POSITIVE_INFINITY)).toThrow(TypeError);
    expect(() => canonicalize(1n)).toThrow(TypeError);
  });
});

describe("device key (US-P1-OFF-02, TC-P1-OFF-008)", () => {
  it("cannot export the private key", async () => {
    const { privateKey } = await generateDeviceKey();

    expect(privateKey.extractable).toBe(false);
    await expect(crypto.subtle.exportKey("jwk", privateKey)).rejects.toThrow();
    await expect(crypto.subtle.exportKey("pkcs8", privateKey)).rejects.toThrow();
  });

  it("gives away only the public point", async () => {
    const { publicKey } = await generateDeviceKey();

    expect(Object.keys(await publicJwk(publicKey)).sort()).toEqual(["crv", "kty", "x", "y"]);
  });

  it("proves possession of the key at enrollment, bound to the code", async () => {
    const { privateKey, publicKey } = await generateDeviceKey();
    const proof = fromBase64Url(await enrollmentProof(privateKey, "ABCD-EFGH-JKMN-PQRS")) as Uint8Array<ArrayBuffer>;

    expect(proof.length).toBe(64); // IEEE P1363 r‖s, what the server verifies (D26)
    expect(await verify(publicKey, "karry-enroll:ABCD-EFGH-JKMN-PQRS", proof)).toBe(true);
    expect(await verify(publicKey, "karry-enroll:ABCD-EFGH-JKMN-PQRT", proof)).toBe(false);
  });
});

describe("device-signed request (ADR-0028 §3)", () => {
  const request = { deviceId: "0192f0c1-0000-7000-8000-00000000d001", tenantId: "0192f0c1-0000-7000-8000-00000000a001", method: "get", path: "/api/v1/sync/v1/changes", now: Date.UTC(2026, 9, 28, 12) };

  it("is a compact ES256 JWS naming the device, tenant, method and path, valid 5 minutes", async () => {
    const { privateKey, publicKey } = await generateDeviceKey();
    const jws = await deviceAssertion(privateKey, request);
    const [header, payload, signature] = jws.split(".");
    const decode = (part: string | undefined) => JSON.parse(new TextDecoder().decode(fromBase64Url(part ?? ""))) as Record<string, unknown>;

    expect(decode(header)).toEqual({ alg: "ES256", typ: "karry-device+jwt", kid: request.deviceId });
    const claims = decode(payload);
    expect(claims).toMatchObject({ tid: request.tenantId, iat: 1793188800, exp: 1793188800 + 300, htm: "GET", htu: "/api/v1/sync/v1/changes" });
    expect(claims.jti).toMatch(/^[0-9a-f-]{36}$/);
    expect(await verify(publicKey, `${header ?? ""}.${payload ?? ""}`, fromBase64Url(signature ?? "") as Uint8Array<ArrayBuffer>)).toBe(true);
  });

  it("never repeats its ID, and a changed claim no longer verifies", async () => {
    const { privateKey, publicKey } = await generateDeviceKey();
    const [first, second] = await Promise.all([deviceAssertion(privateKey, request), deviceAssertion(privateKey, request)]);
    expect(first).not.toBe(second);

    const [header, payload, signature] = first.split(".");
    const forged = JSON.parse(new TextDecoder().decode(fromBase64Url(payload ?? ""))) as Record<string, unknown>;
    forged.htu = "/api/v1/identity/users";
    const forgedPayload = btoa(JSON.stringify(forged)).replace(/=+$/, "");
    expect(await verify(publicKey, `${header ?? ""}.${forgedPayload}`, fromBase64Url(signature ?? "") as Uint8Array<ArrayBuffer>)).toBe(false);
  });
});

describe("offline PIN check (US-P1-OFF-04 AC1, ADR-0028 §4)", () => {
  const pins = vectors("pin-verifier.json") as { vectors: { pin: string; salt: string; iterations: number; pinKey: string; verifier: string }[] };

  it.each(pins.vectors.map((v) => [v.iterations, v] as const))("reproduces the independent verifier (%i iterations)", async (_n, v) => {
    const key = await importPinKey(fromBase64(v.pinKey) as Uint8Array<ArrayBuffer>);

    expect(await pinVerifier(key, v.pin, v.salt, v.iterations)).toBe(v.verifier);
    expect(await checkPin(key, v.pin, v)).toBe(true);
  });

  it("refuses a wrong PIN and a verifier made with another tablet's key", async () => {
    const v = pins.vectors[0];
    if (!v) throw new Error("no vector");
    const key = await importPinKey(fromBase64(v.pinKey) as Uint8Array<ArrayBuffer>);
    const otherTablet = await importPinKey(new Uint8Array(32));

    expect(await checkPin(key, "583921", v)).toBe(false);
    expect(await checkPin(otherTablet, v.pin, v)).toBe(false);
  });

  it("keeps the PIN key inside the browser", async () => {
    const key = await importPinKey(new Uint8Array(32).fill(7));

    expect(key.extractable).toBe(false);
    await expect(crypto.subtle.exportKey("raw", key)).rejects.toThrow();
  });
});
