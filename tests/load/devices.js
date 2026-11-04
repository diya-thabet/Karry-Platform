// H-L1: the start of a shift with 500 enrolled tablets of one site, every request signed by the tablet's own key.
//  - burst: every tablet asks for its snapshot in the same second (worst case). Pass: no failed request, every tablet
//    gets only its own site; p95 is recorded, with an alarm at 3 s against regressions.
//  - shift: the tablets come in over a minute, then pull deltas every 2-4 s (the app does it every 5 min, so this is
//    about 100 times the real rate). Pass: no failed request, p95 under 800 ms.
// Tablets are enrolled in setup by a site manager (real Keycloak sign-in) and revoked in teardown.
// Docs: docs/05-quality/load-testing.md.
import http from "k6/http";
import { check, fail, sleep } from "k6";
import { Counter } from "k6/metrics";
import { api, bearer, deviceAuthorization, enrolTablet, latencyGate, newTabletKey, signIn, tabletKey } from "./lib/karry.js";

const tablets = Number(__ENV.TABLETS ?? 500);
const company = __ENV.KARRY_COMPANY ?? "demo-carriere-a";
const manager = `site-manager@${company}.karry.test`;
const changes = "/api/v1/sync/changes";
const wrongSite = new Counter("wrong_site");
// Thresholds on rates pass on no data at all: the run must also show every burst pull answered.
const burstPulled = new Counter("burst_pulls_ok");
const shiftPulled = new Counter("shift_pulls_ok");

export const options = {
  setupTimeout: "15m",
  teardownTimeout: "10m",
  scenarios: {
    burst: { executor: "per-vu-iterations", vus: tablets, iterations: 1, maxDuration: "2m", exec: "burst" },
    shift: {
      executor: "ramping-vus", exec: "shift", startTime: "40s", startVUs: 0,
      stages: [{ duration: "60s", target: tablets }, { duration: "60s", target: tablets }, { duration: "5s", target: 0 }],
    },
  },
  thresholds: {
    "http_req_failed{scenario:burst}": ["rate==0"],
    "http_req_duration{scenario:burst}": latencyGate("p(95)<3000"),
    burst_pulls_ok: [`count==${tablets}`],
    "http_req_failed{scenario:shift}": ["rate==0"],
    "http_req_duration{scenario:shift}": latencyGate("p(95)<800"),
    shift_pulls_ok: [`count>=${tablets}`],
    wrong_site: ["count==0"],
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

  return { site, tablets: enrolled };
}

let key; // this virtual user's tablet key, imported once
let cursor = null;

export async function burst(data) {
  await pull(data, burstPulled);
}

export async function shift(data) {
  await pull(data, shiftPulled);
  sleep(2 + Math.random() * 2);
}

async function pull(data, pulled) {
  const tablet = data.tablets[(__VU - 1) % data.tablets.length];
  key ??= await tabletKey(tablet.jwk);
  const response = http.get(`${api}${changes}${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ""}`, {
    headers: { Authorization: await deviceAuthorization(tablet, key, "GET", changes) },
    tags: { name: cursor ? "delta" : "snapshot" },
  });
  const ok = check(response, { "changes 200": (r) => r.status === 200 });
  if (ok) {
    const body = response.json();
    if (!cursor) {
      // Positive control: a snapshot holds the tablet's site, so the wrong-site count below looks at real data.
      check(body, { "snapshot holds the tablet's site": (b) => (b.changes.sites.upserts ?? []).some((s) => s.id === tablet.siteId) });
    }
    const foreign = (body.changes.sites.upserts ?? []).filter((s) => s.id !== tablet.siteId).length
      + (body.changes.assets.upserts ?? []).filter((a) => a.siteId !== tablet.siteId).length;
    wrongSite.add(foreign);
    pulled.add(1);
    cursor = body.cursor;
  }
}

export function teardown(data) {
  const person = signIn(manager, company);
  for (const tablet of data.tablets) {
    http.post(`${api}/api/v1/identity/devices/${tablet.deviceId}/revoke`, null, bearer(person));
  }
}

