// TC-P1-TEN-005 (H-L2) and the spike (H-L4): people of two companies call the API at the same time, signed in through
// Keycloak like the portal. Every answer is searched for the other company's identifiers (tenant ID, slug, name, the
// IDs of its sites, materials and machines). Pass: 0 leaks, p95 under 300 ms, under 0.1 % failed requests, at least
// 10,000 requests checked; the spike (0 to 300 users in 10 s) never gets a 5xx. Docs: docs/05-quality/load-testing.md.
import http from "k6/http";
import encoding from "k6/encoding";
import { check, fail, sleep } from "k6";
import { Counter } from "k6/metrics";
import { api, bearer, signIn } from "./lib/karry.js";

const companies = ["demo-carriere-a", "demo-carriere-b"];
// Office roles without a second factor: the only demo accounts a script can sign in.
const roles = ["site-manager", "maintenance-planner", "commercial"];
const reads = [
  "/api/v1/tenancy/current", "/api/v1/organization/sites", "/api/v1/materials", "/api/v1/assets", "/api/v1/assets/types",
  "/api/v1/identity/me", "/api/v1/identity/me/access", "/api/v1/identity/users", "/api/v1/identity/roles", "/api/v1/identity/devices",
  "/api/v1/billing/entitlements", "/api/v1/billing/usage", "/api/v1/flags", "/api/v1/imports",
];
const requests = Number(__ENV.REQUESTS ?? 10_000);
const leaks = new Counter("cross_tenant_leaks");
const checked = new Counter("answers_checked");
const serverErrors = new Counter("server_errors");

// One scenario per run (SCENARIO=mixed, the default, or spike), so the spike never inflates the mixed run's numbers.
const scenario = __ENV.SCENARIO ?? "mixed";
const spikeUsers = Number(__ENV.SPIKE_VUS ?? 300);
const scenarios = {
  mixed: { executor: "shared-iterations", vus: Number(__ENV.VUS ?? 50), iterations: requests, maxDuration: "4m", exec: "mixed" },
  spike: {
    executor: "ramping-vus", exec: "spike", startVUs: 0,
    stages: [{ duration: "10s", target: spikeUsers }, { duration: "20s", target: spikeUsers }, { duration: "5s", target: 0 }],
  },
};
const thresholds = {
  mixed: {
    cross_tenant_leaks: ["count==0"],
    answers_checked: [`count>=${requests}`],
    "http_req_duration{scenario:mixed}": ["p(95)<300"],
    "http_req_failed{scenario:mixed}": ["rate<0.001"],
    server_errors: ["count==0"],
  },
  spike: { server_errors: ["count==0"], cross_tenant_leaks: ["count==0"], "answers_checked": ["count>0"] },
};

export const options = { setupTimeout: "5m", scenarios: { [scenario]: scenarios[scenario] }, thresholds: thresholds[scenario] };

const claims = (token) => JSON.parse(encoding.b64decode(token.split(".")[1], "rawurl", "s"));
const ids = (body) => (body?.items ?? []).map((item) => item.id);

export function setup() {
  const people = [];
  const markers = {};
  for (const company of companies) {
    const signedIn = roles.map((role) => signIn(`${role}@${company}.karry.test`, company));
    const admin = signedIn[0];
    const tenant = http.get(`${api}/api/v1/tenancy/current`, bearer(admin)).json();
    markers[company] = [
      claims(admin.accessToken).tenant_id, company, tenant.name,
      ...ids(http.get(`${api}/api/v1/organization/sites`, bearer(admin)).json()),
      ...ids(http.get(`${api}/api/v1/materials`, bearer(admin)).json()),
      ...ids(http.get(`${api}/api/v1/assets`, bearer(admin)).json()),
    ].filter(Boolean);
    if (markers[company].length < 6) fail(`${company}: too few identifiers to look for (${markers[company].length}); is the demo seeded?`);
    // Positive control: the search finds a company's identifiers in its own answers, so 0 leaks means something.
    const own = http.get(`${api}/api/v1/organization/sites`, bearer(admin)).body;
    if (!markers[company].some((marker) => marker !== company && own.includes(marker))) fail(`${company}: the leak search does not see the company's own identifiers`);
    for (const person of signedIn) {
      // Only what this person may read: a 403 is the permission check doing its job, not a leak test.
      const probe = { ...bearer(person), responseCallback: http.expectedStatuses(200, 403) };
      const allowed = reads.filter((path) => http.get(`${api}${path}`, probe).status === 200);
      people.push({ ...person, allowed });
    }
  }

  const soonest = Math.min(...people.map((p) => p.expiresAt));
  if (soonest - Date.now() < 4.5 * 60_000) fail("the access tokens expire before the run ends; lengthen the realm's token lifespan for load runs");
  return { people, markers };
}

function call(data, scenario) {
  const person = data.people[Math.floor(Math.random() * data.people.length)];
  const path = person.allowed[Math.floor(Math.random() * person.allowed.length)];
  const response = http.get(`${api}${path}`, { ...bearer(person), tags: { name: path } });
  if (response.status >= 500) serverErrors.add(1, { scenario, path });
  // In the spike a 429 is the right answer (rate limits); only a 5xx fails it.
  if (scenario === "spike" ? response.status !== 200 : !check(response, { "200": (r) => r.status === 200 })) return;
  const other = companies.find((c) => c !== person.company);
  const found = data.markers[other].filter((marker) => response.body.includes(marker));
  if (found.length > 0) {
    leaks.add(found.length, { path, from: other });
    console.error(`LEAK: ${person.email} ${path} returned ${other}: ${found.join(", ")}`);
  }

  checked.add(1);
}

export function mixed(data) {
  call(data, "mixed");
}

export function spike(data) {
  call(data, "spike");
  sleep(0.3 + Math.random() * 0.7); // a person clicks, reads, clicks again
}
