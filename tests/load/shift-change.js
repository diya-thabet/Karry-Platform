// P2.12a burst (sub-phases §P2.12a, M2 exit gate): the shift change at one site. Each tablet, enrolled in setup by a
// site manager, pulls its site, then sends what a shift change leaves on it: 5 shift entries started and ended
// (10 commands, each signed as the app signs it: RFC 8785 record hash, envelope signed by the tablet's key, the command
// signed again), with a ~200 KB photo per command and the operator's handwritten signature (strokes and image), all
// uploaded by pre-signed PUT before the batches that cite them.
// Setup registers one machine with an hour meter per tablet (two tablets on one meter at once are a conflict).
// Pass: no 5xx and no failed request, every command stored at once (accepted, or kept with its integrity flags;
// never waiting, refused or duplicated), p95 batch
// < 1 s (a latency gate, reported only on shared runners). The queue gate (outboxes drained within 10 s of the end)
// is tests/load/outbox-drained.sh, run after this by tests/load/run.sh.
// Docs: docs/05-quality/load-testing.md. Signing code: crypto.gen.js, the bundle of @karry/crypto (the app's own).
import "./lib/k6-shims.js";
import http from "k6/http";
import { check, fail, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import { canonicalize, handwrittenHashes, payloadHash, sha256Bytes, signEnvelope, sign, toBase64Url } from "./lib/crypto.gen.js";
import { api, bearer, deviceAuthorization, enrolTablet, latencyGate, newTabletKey, signIn, tabletKey } from "./lib/karry.js";

const tablets = Number(__ENV.BURST_TABLETS ?? 300);
const entries = Number(__ENV.BURST_ENTRIES ?? 5); // started and ended: two commands each
const perBatch = Number(__ENV.BURST_BATCH ?? 5);
const arrivalSeconds = Number(__ENV.BURST_ARRIVAL_SECONDS ?? 120); // the tablets come back over two minutes
const photoBytes = Number(__ENV.BURST_PHOTO_BYTES ?? 200 * 1024);
const company = __ENV.KARRY_COMPANY ?? "demo-carriere-a";
const manager = `site-manager@${company}.karry.test`;
const commands = tablets * entries * 2;

const accepted = new Counter("commands_stored"); // accepted, or stored with the flags it raised ("conflict")
const flagged = new Counter("flags_raised");
const notAccepted = new Counter("commands_not_accepted");
const serverErrors = new Counter("server_errors");
const uploaded = new Counter("blobs_uploaded");
const batchDuration = new Trend("batch_duration", true);

export const options = {
  setupTimeout: "15m",
  teardownTimeout: "10m",
  scenarios: {
    shift_change: { executor: "per-vu-iterations", vus: tablets, iterations: 1, maxDuration: "15m", exec: "shiftChange" },
  },
  thresholds: {
    http_req_failed: ["rate==0"],
    server_errors: ["count==0"],
    commands_stored: [`count==${commands}`],
    commands_not_accepted: ["count==0"],
    blobs_uploaded: [`count==${commands * 3}`],
    batch_duration: latencyGate("p(95)<1000"),
    checks: ["rate==1"],
  },
};

export async function setup() {
  let person = signIn(manager, company);
  const site = http.get(`${api}/api/v1/organization/sites`, bearer(person)).json().items[0].id;
  const enrolled = [];
  for (let i = 0; i < tablets; i++) {
    if (Date.now() > person.expiresAt - 30_000) person = signIn(manager, company);
    const created = http.post(`${api}/api/v1/identity/devices/enrollments`, JSON.stringify({ siteId: site }), {
      headers: { ...bearer(person).headers, "Content-Type": "application/json" },
    });
    check(created, { "enrolment code 201": (r) => r.status === 201 }) || fail(`enrolment code ${i}: ${created.status} ${created.body}`);
    enrolled.push(await enrolTablet(company, created.json().code, await newTabletKey(), i));
  }

  // One machine with its hour meter per tablet, as at a real shift change (two tablets on one meter at once are a
  // conflict by design). Codes are new on every run.
  const run = Date.now().toString(36).slice(-5).toUpperCase();
  const machines = [];
  for (let i = 0; i < tablets; i++) {
    if (Date.now() > person.expiresAt - 30_000) person = signIn(manager, company);
    const code = `LD${run}-${i}`;
    const registered = http.post(`${api}/api/v1/assets`, JSON.stringify({
      code, name: `Charge ${i}`, type: "excavator", make: null, model: null, serialNumber: null, plate: null, year: null, acquiredOn: null,
      siteId: site, meters: [{ kind: "hours", initialReading: 100000 + i * 1000, installedOn: null }],
    }), { headers: { ...bearer(person).headers, "Content-Type": "application/json" } });
    check(registered, { "machine 201": (r) => r.status === 201 }) || fail(`machine ${i}: ${registered.status} ${registered.body}`);
    machines.push(code);
  }

  return { site, tablets: enrolled, machines };
}

/** A UUIDv7 (RFC 9562), lowercase, as the app makes them (@karry/offline uuidv7). */
function uuidv7() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  let ms = Date.now();
  for (let i = 5; i >= 0; i--) {
    bytes[i] = ms % 256;
    ms = Math.floor(ms / 256);
  }

  bytes[6] = 0x70 | (bytes[6] & 0x0f);
  bytes[8] = 0x80 | (bytes[8] & 0x3f);
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Unique bytes standing for a photo (the server checks the declared type and the hash, not the pixels). */
function photo() {
  const bytes = new Uint8Array(photoBytes);
  for (let at = 0; at < bytes.length; at += 65536) {
    crypto.getRandomValues(bytes.subarray(at, Math.min(at + 65536, bytes.length)));
  }

  bytes.set([0xff, 0xd8, 0xff, 0xe0]); // a JPEG's first bytes
  return bytes;
}

const hex = (hash) => hash.slice("sha256:".length);

async function send(tablet, key, method, path, body) {
  const response = http.request(method, `${api}${path}`, body === undefined ? null : JSON.stringify(body), {
    headers: { Authorization: await deviceAuthorization(tablet, key, method, path), "Content-Type": "application/json", "Idempotency-Key": crypto.randomUUID() },
    tags: { name: path.replace(/[0-9a-f-]{36}/g, "{id}") },
  });
  if (response.status >= 500) serverErrors.add(1);
  return response;
}

/** Presign, then PUT the exact bytes unless the store already holds them (as the app's uploadBlob). */
async function upload(tablet, key, content, contentType) {
  const sha256 = hex(await sha256Bytes(content));
  const presigned = await send(tablet, key, "POST", "/api/v1/sync/blobs/presign", { sha256, size: content.length, contentType });
  if (!check(presigned, { "presign 200": (r) => r.status === 200 })) return;
  const target = presigned.json();
  if (!target.exists) {
    const put = http.put(target.url, content.buffer, { headers: target.headers ?? {}, tags: { name: "evidence PUT" } });
    if (put.status >= 500) serverErrors.add(1);
    if (!check(put, { "evidence PUT 2xx": (r) => r.status >= 200 && r.status < 300 })) return;
  }

  uploaded.add(1);
}

/** One signed command with its evidence (the app's signAndEnqueue and enqueue, without the outbox). */
async function signed(tablet, key, operator, sequence, type, record, version, photoRef) {
  const now = new Date().toISOString();
  const strokes = JSON.stringify({ width: 600, height: 200, strokes: [[{ x: 10, y: 20, t: 0 }, { x: 40 + sequence, y: 60, t: 16 }]] });
  const png = photo().subarray(0, 2048);
  png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); // a PNG's signature
  const envelope = {
    v: 1, envelopeId: uuidv7(), recordType: "fieldops.shift_entry", recordId: record.rootId, recordVersion: version,
    payloadHash: await payloadHash(record), evidence: [photoRef.ref], handwritten: await handwrittenHashes(strokes, png),
    signer: { userId: operator, role: "operator", deviceId: tablet.deviceId }, context: { deviceTime: now, appVersion: "load" },
  };
  const signedEnvelope = await signEnvelope(key, envelope);
  const payload = {
    record, envelope: signedEnvelope.envelope, deviceSignature: signedEnvelope.deviceSignature,
    captures: [{ sha256: photoRef.sha256, capturedAt: now, deviceId: tablet.deviceId, appVersion: "load", width: 1600, height: 1200 }],
    clock: { offsetMs: 0 },
  };
  const command = { commandId: uuidv7(), sequence, type, payload, deviceTime: now };
  const signature = toBase64Url(await sign(key, canonicalize(command)));
  const blobs = [
    { content: photoRef.content, contentType: "image/jpeg" },
    { content: new TextEncoder().encode(strokes), contentType: "application/json" },
    { content: png, contentType: "image/png" },
  ];
  return { command: { ...command, signature }, blobs };
}

export async function shiftChange(data) {
  const tablet = data.tablets[(__VU - 1) % data.tablets.length];
  const key = await tabletKey(tablet.jwk);
  sleep(Math.random() * arrivalSeconds);

  // What the tablet knows of its site: its machines' meters and its field users (the snapshot of a first pull).
  const pulled = await send(tablet, key, "GET", "/api/v1/sync/changes");
  if (!check(pulled, { "snapshot 200": (r) => r.status === 200 })) return;
  const changes = pulled.json().changes;
  const machine = (changes.assets?.upserts ?? []).find((a) => a.code === data.machines[(__VU - 1) % data.machines.length]);
  const meter = (changes.meters?.upserts ?? []).find((m) => machine && m.assetId === machine.id && m.status === "ok" && m.kind === "hours");
  const users = changes.users?.upserts ?? [];
  if (!check(null, { "the tablet's machine, its hour meter and a field user": () => meter !== undefined && users.length > 0 })) return;
  const operator = users[(__VU - 1) % users.length].id;

  // Five entries one after the other on the tablet's machine, from the meter's initial reading.
  const work = [];
  let value = 100000 + ((__VU - 1) % data.machines.length) * 1000;
  let sequence = 0;
  for (let e = 0; e < entries; e++) {
    const rootId = uuidv7();
    const startedAt = new Date(Date.now() - (entries - e) * 3_600_000).toISOString();
    const startPhoto = photo();
    const startHash = hex(await sha256Bytes(startPhoto));
    const start = {
      rootId, version: 1, assetId: meter.assetId, meterId: meter.id, startedAt, startValue: value.toFixed(1),
      fuelStartPercent: 80, startPhoto: `sha256:${startHash}`,
    };
    work.push(await signed(tablet, key, operator, ++sequence, "fieldops.shift_entry.start.v1", start, 1, { ref: `sha256:${startHash}`, sha256: startHash, content: startPhoto }));
    value += 0.8;
    const endPhoto = photo();
    const endHash = hex(await sha256Bytes(endPhoto));
    const end = {
      rootId, version: 2, endedAt: new Date(Date.parse(startedAt) + 2_880_000).toISOString(), endValue: value.toFixed(1),
      fuelEndPercent: 60, endPhoto: `sha256:${endHash}`,
    };
    work.push(await signed(tablet, key, operator, ++sequence, "fieldops.shift_entry.end.v1", end, 2, { ref: `sha256:${endHash}`, sha256: endHash, content: endPhoto }));
  }

  // As the app: the evidence of a batch first, then the batch; the receipts answer each command.
  for (let at = 0; at < work.length; at += perBatch) {
    const batch = work.slice(at, at + perBatch);
    for (const item of batch) {
      for (const blob of item.blobs) await upload(tablet, key, blob.content, blob.contentType);
    }

    const started = Date.now();
    const response = await send(tablet, key, "POST", "/api/v1/sync/batches", { commands: batch.map((item) => item.command) });
    batchDuration.add(Date.now() - started);
    if (!check(response, { "batch 200": (r) => r.status === 200 })) continue;
    for (const result of response.json().results) {
      // "conflict" is stored too: the entry is kept with the integrity flags it raised (ADR-0031 §8, ADR-0036).
      if (result.status === "accepted" || result.status === "conflict") {
        accepted.add(1);
        for (const flag of result.flags ?? []) flagged.add(1, { flag });
      } else {
        notAccepted.add(1, { status: result.status, code: result.code ?? "none" });
        console.warn(`tablet ${__VU}: ${result.status} ${result.code ?? ""}`);
      }
    }
  }
}

export function teardown(data) {
  const person = signIn(manager, company);
  for (const tablet of data.tablets) {
    http.post(`${api}/api/v1/identity/devices/${tablet.deviceId}/revoke`, null, bearer(person));
  }
}
