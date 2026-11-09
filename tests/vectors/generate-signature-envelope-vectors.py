"""Signature envelope v1 vectors (ADR-0033 §1), computed with Python (json + cryptography): an implementation
independent of both the browser (@karry/crypto) and the server (Trust), which must each reproduce them.

For each valid case: payloadHash = "sha256:" + hex(SHA-256(RFC 8785 of the record's payload)); canonical = RFC 8785 of
the envelope; envelopeHash = "sha256:" + hex(SHA-256(canonical)); deviceSignature = ES256 (IEEE P1363, base64url) over
canonical by the device key, whose public JWK is given. Both sides must compute the same text and hashes, verify the
signature, and refuse it once one character of the envelope changes. Each invalid case must be refused before signing
(tablet) and on receipt (server). The envelopes hold only strings, integers and short decimals, so json.dumps with
sorted keys and no spaces is their RFC 8785 form (checked below on a sample of the keys).

Run: python3 tests/vectors/generate-signature-envelope-vectors.py > tests/vectors/signature-envelope.json
"""
import base64
import copy
import hashlib
import json

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, utils


def canonical(value) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def sha256(text: str) -> str:
    return "sha256:" + hashlib.sha256(text.encode()).hexdigest()


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


# A fixed test key: the private scalar is printed nowhere else and signs nothing but these vectors.
key = ec.derive_private_key(0x6B4E2A1F0C9D8E7F6A5B4C3D2E1F0A9B8C7D6E5F4A3B2C1D0E9F8A7B6C5D4E3F, ec.SECP256R1())
numbers = key.public_key().public_numbers()
jwk = {"kty": "EC", "crv": "P-256", "x": b64url(numbers.x.to_bytes(32, "big")), "y": b64url(numbers.y.to_bytes(32, "big"))}


def sign(text: str) -> str:
    r, s = utils.decode_dss_signature(key.sign(text.encode(), ec.ECDSA(hashes.SHA256())))
    return b64url(r.to_bytes(32, "big") + s.to_bytes(32, "big"))


payload = {"assetId": "01970000-0000-7000-8000-000000000a01", "meter": {"unit": "h", "value": "1234.5"}, "fuelLevel": "3/4", "remarks": "RAS — vérifié"}
payload_hash = sha256(canonical(payload))

minimal = {
    "v": 1,
    "envelopeId": "01970000-0000-7000-8000-0000000e0001",
    "recordType": "fieldops.shift_entry",
    "recordId": "01970000-0000-7000-8000-0000000f0001",
    "recordVersion": 1,
    "payloadHash": payload_hash,
    "signer": {"userId": "01970000-0000-7000-8000-000000000501", "role": "operator", "deviceId": "01970000-0000-7000-8000-000000000d01"},
    "context": {"deviceTime": "2026-11-09T06:02:11.250+00:00", "appVersion": "1.4.0"},
}
full = copy.deepcopy(minimal)
full["envelopeId"] = "01970000-0000-7000-8000-0000000e0002"
full["evidence"] = ["sha256:" + hashlib.sha256(b"start photo").hexdigest(), "sha256:" + hashlib.sha256(b"meter photo").hexdigest()]
full["handwritten"] = {"strokesHash": "sha256:" + hashlib.sha256(b"strokes").hexdigest(), "imageHash": "sha256:" + hashlib.sha256(b"png").hexdigest()}
full["context"]["geo"] = {"lat": 9.5092, "lon": -13.7122, "acc": 8}
full["context"]["deviceTime"] = "2026-11-09T22:47:03+01:00"

valid = []
for name, envelope in (("minimal", minimal), ("full: evidence, handwriting and geo", full)):
    text = canonical(envelope)
    tampered = text.replace('"recordVersion":1', '"recordVersion":2')
    assert tampered != text
    valid.append({
        "name": name,
        "envelope": envelope,
        "canonical": text,
        "envelopeHash": sha256(text),
        "deviceSignature": sign(text),
        "tamperedCanonical": tampered,
    })


def broken(name, change):
    envelope = copy.deepcopy(full)
    change(envelope)
    return {"name": name, "envelope": envelope}


def set_path(path, value):
    def change(envelope):
        target = envelope
        for part in path[:-1]:
            target = target[part]
        target[path[-1]] = value
    return change


def drop(path):
    def change(envelope):
        target = envelope
        for part in path[:-1]:
            target = target[part]
        del target[path[-1]]
    return change


invalid = [
    broken("another version", set_path(["v"], 2)),
    broken("a null member instead of an absent one", set_path(["handwritten"], None)),
    broken("an unknown member", set_path(["comment"], "x")),
    broken("an unknown member in the signer", set_path(["signer", "name"], "Mamadou")),
    broken("no payload hash", drop(["payloadHash"])),
    broken("a payload hash without its algorithm", set_path(["payloadHash"], payload_hash[len("sha256:"):])),
    broken("a payload hash in upper case", set_path(["payloadHash"], payload_hash.upper())),
    broken("an envelope ID that is not a UUID", set_path(["envelopeId"], "e-1")),
    broken("an envelope ID in upper case", set_path(["envelopeId"], minimal["envelopeId"].upper())),
    broken("a record type without its module", set_path(["recordType"], "shift_entry")),
    broken("record version 0", set_path(["recordVersion"], 0)),
    broken("a record version as a string", set_path(["recordVersion"], "1")),
    broken("an empty role", set_path(["signer", "role"], "")),
    broken("a device time without an offset", set_path(["context", "deviceTime"], "2026-11-09T06:02:11")),
    broken("no app version", drop(["context", "appVersion"])),
    broken("evidence named twice", set_path(["evidence"], [full["evidence"][0], full["evidence"][0]])),
    broken("empty evidence list (leave it out instead)", set_path(["evidence"], [])),
    broken("handwriting without its image", drop(["handwritten", "imageHash"])),
    broken("a record type followed by a new line", set_path(["recordType"], "fieldops.shift_entry\n")),
    broken("a device time in Arabic-Indic digits", set_path(["context", "deviceTime"], "٢٠٢٦-11-09T06:02:11Z")),
    broken("a latitude out of range", set_path(["context", "geo", "lat"], 91)),
    broken("a negative accuracy", set_path(["context", "geo", "acc"], -1)),
]

# json.dumps(sort_keys) sorts by code point and RFC 8785 by UTF-16 code unit: the same for ASCII member names.
assert all(k.isascii() for k in json.dumps([payload, full], sort_keys=True).split('"')[1::2])

print(json.dumps({
    "description": "Signature envelope v1 vectors (ADR-0033 §1) generated by tests/vectors/generate-signature-envelope-vectors.py",
    "devicePublicKey": jwk,
    "payload": payload,
    "payloadCanonical": canonical(payload),
    "payloadHash": payload_hash,
    "valid": valid,
    "invalid": invalid,
}, ensure_ascii=False, indent=1))
