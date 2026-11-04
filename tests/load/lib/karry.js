// Shared helpers of the load tests (P1.18a, docs/05-quality/load-testing.md): real Keycloak sign-in (Authorization
// Code + PKCE, as the portal does) and tablets signing every request (ADR-0028 §3), so the load goes through the same
// authentication, tenant resolution, row-level security and rate limits as production traffic.
import http from "k6/http";
import encoding from "k6/encoding";
import k6crypto from "k6/crypto";
import { check, fail } from "k6";

export const api = __ENV.KARRY_API ?? "http://127.0.0.1:5000";
export const keycloak = __ENV.KARRY_KEYCLOAK ?? "http://localhost:8080";

/**
 * A latency target as a k6 threshold, or none with LATENCY_GATES=report: on a shared CI runner (2 vCPU, noisy neighbours)
 * response times say nothing about Karry, so the nightly gates on correctness (errors, leaks, wrong site, the ledgers)
 * and only reports the times. The targets gate on known hardware: the owner's machine now, staging in P1.18b.
 */
export const latencyGate = (rule) => (__ENV.LATENCY_GATES === "report" ? [] : [rule]);
const password = __ENV.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only"; // the local stack's demo password (DemoSeed)

function ascii(text) {
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i);
  return bytes;
}
const b64url = (bytes) => encoding.b64encode(bytes instanceof ArrayBuffer ? bytes : bytes.buffer, "rawurl");

/** Cookies by hand: Keycloak marks them Secure even on loopback HTTP, which a cookie jar would not send back. */
function remember(cookies, response) {
  for (const header of [].concat(response.headers["Set-Cookie"] ?? [])) {
    for (const line of header.split(/\n/)) {
      const [pair] = line.split(";");
      const at = pair.indexOf("=");
      if (at > 0) cookies[pair.slice(0, at).trim()] = pair.slice(at + 1).trim();
    }
  }
}

const cookieHeader = (cookies) => Object.entries(cookies).map(([k, v]) => `${k}=${v}`).join("; ");

/**
 * Signs a demo user in through the portal's client and returns their tokens. Only accounts without a second factor
 * can do this unattended (site managers, maintenance planners, commercial staff of the demo companies).
 */
export function signIn(email, company) {
  const verifier = b64url(crypto.getRandomValues(new Uint8Array(48)));
  const challenge = b64url(k6crypto.sha256(verifier, "binary")); // setup is synchronous; WebCrypto's digest is not
  const redirect = `http://${company}.localhost:5173/auth/callback`;
  const jar = new http.CookieJar();
  const cookies = {};
  const authorize = `${keycloak}/realms/karry/protocol/openid-connect/auth?client_id=karry-portal&response_type=code&scope=openid`
    + `&redirect_uri=${encodeURIComponent(redirect)}&code_challenge=${challenge}&code_challenge_method=S256&state=load&nonce=load`;
  const page = http.get(authorize, { redirects: 0, jar });
  remember(cookies, page);
  const action = /<form[^>]*?action="([^"]+)"/s.exec(page.body)?.[1]?.replaceAll("&amp;", "&");
  if (!action) fail(`${email}: no login form (status ${page.status})`);
  const posted = http.post(action, { username: email, password, credentialId: "" }, { redirects: 0, jar, headers: { Cookie: cookieHeader(cookies) } });
  const code = /[?&]code=([^&]+)/.exec(posted.headers.Location ?? "")?.[1];
  if (!code) fail(`${email}: sign-in did not return a code (status ${posted.status}); is the demo seeded and the account without MFA?`);
  const tokens = http.post(`${keycloak}/realms/karry/protocol/openid-connect/token`, {
    grant_type: "authorization_code", client_id: "karry-portal", code: decodeURIComponent(code), redirect_uri: redirect, code_verifier: verifier,
  }, { jar });
  check(tokens, { "token exchange 200": (r) => r.status === 200 }) || fail(`${email}: token exchange ${tokens.status}`);
  const body = tokens.json();
  return { email, company, accessToken: body.access_token, expiresAt: Date.now() + body.expires_in * 1000 };
}

export const bearer = (person) => ({ headers: { Authorization: `Bearer ${person.accessToken}`, "Idempotency-Key": crypto.randomUUID() } });

/**
 * RFC 7518 §6.2.1.2 wants each P-256 JWK member as the full 32 bytes; k6 2.3.0 drops leading zero bytes (about one key
 * in 200), which Karry rightly refuses (`devices.key_invalid`). Browsers pad; this does the same for k6.
 */
function fullLength(member) {
  const bytes = new Uint8Array(encoding.b64decode(member, "rawurl"));
  if (bytes.length === 32) return member;
  const padded = new Uint8Array(32);
  padded.set(bytes, 32 - bytes.length);
  return b64url(padded);
}

/** A new tablet's key pair: exported as JWK so setup can hand it to the virtual users (a test tablet only). */
export async function newTabletKey() {
  const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const jwk = await crypto.subtle.exportKey("jwk", pair.privateKey);
  return { pair, jwk: { ...jwk, x: fullLength(jwk.x), y: fullLength(jwk.y), d: fullLength(jwk.d) } };
}

/** Enrols a tablet with a code a site manager created (ADR-0028 §2): the proof is the code signed by its key. */
export async function enrolTablet(company, code, key, index) {
  const proof = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key.pair.privateKey, ascii(`karry-enroll:${code}`));
  const { kty, crv, x, y } = key.jwk;
  const response = http.post(`${api}/api/v1/identity/devices/enroll`, JSON.stringify({
    tenant: company, code, publicKey: { kty, crv, x, y }, proof: b64url(proof),
    name: `Charge ${index}`, model: "k6", platform: "load test", appVersion: "load",
  }), { headers: { "Content-Type": "application/json", "Idempotency-Key": crypto.randomUUID() } });
  if (response.status !== 201) fail(`enrolment ${index}: ${response.status} ${response.body}`);
  const body = response.json();
  return { deviceId: body.deviceId, tenantId: body.tenantId, siteId: body.siteId, jwk: key.jwk };
}

/** The signing key of a tablet handed over by setup (imported once per virtual user). */
export function tabletKey(jwk) {
  return crypto.subtle.importKey("jwk", jwk, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
}

/** `Authorization: Device <compact JWS>` for one request: ES256 over {tid, iat, exp, jti, htm, htu}. */
export async function deviceAuthorization(tablet, key, method, path) {
  const now = Math.floor(Date.now() / 1000);
  const header = b64url(ascii(JSON.stringify({ alg: "ES256", typ: "karry-device+jwt", kid: tablet.deviceId })));
  const payload = b64url(ascii(JSON.stringify({ tid: tablet.tenantId, iat: now, exp: now + 60, jti: crypto.randomUUID(), htm: method, htu: path })));
  const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, ascii(`${header}.${payload}`));
  return `Device ${header}.${payload}.${b64url(signature)}`;
}
