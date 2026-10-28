import type { components } from "@karry/api-client";
import { enrollmentProof, fromBase64, generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import type { DeviceRecord, KarryOfflineDb } from "@karry/offline";

export const enrollPath = "/api/v1/identity/devices/enroll";

type Enrolled = components["schemas"]["DeviceEnrolledResponse"];

/** Why an enrollment did not succeed, as the enrollment screen explains it. */
export type EnrollFailure = "expired" | "unknown" | "invalid" | "offline" | "failed";

export interface EnrollInput {
  /** The company's slug, from the tablet's address (or typed on a bare local address). */
  company: string;
  /** As typed or scanned; the server ignores dashes, spaces and case, and reads O as 0 and I or L as 1. */
  code: string;
  name: string;
}

/** What the tablet says about itself at enrollment (shown in the portal's tablet list). */
export interface DeviceInfo {
  model: string;
  platform: string;
  appVersion: string;
}

/**
 * The company named by the tablet's address, `<slug>.<field address>` (ADR-0028 §2, as the sign-in page does); null on
 * an address without one (`localhost`, an IP address in local development), where the enrollment screen asks for it.
 */
export function companyOf(hostname: string): string | null {
  const [first, ...rest] = hostname.split(".");
  return first && rest.length > 0 && /^[a-z][a-z0-9-]*$/.test(first) ? first : null;
}

/** The code of an enrollment link `…/enroll#<code>` (the QR code of the portal), or null. */
export function codeOf(location: { pathname: string; hash: string }): string | null {
  return location.pathname === "/enroll" && location.hash.length > 1 ? decodeURIComponent(location.hash.slice(1)) : null;
}

/** Model and platform as far as the browser tells (Chromium's client hints; elsewhere a generic value). */
export async function deviceInfo(appVersion: string): Promise<DeviceInfo> {
  const hints = (navigator as Navigator & { userAgentData?: { platform: string; getHighEntropyValues(keys: string[]): Promise<{ model?: string; platformVersion?: string }> } }).userAgentData;
  try {
    const values = await hints?.getHighEntropyValues(["model", "platformVersion"]);
    const model = values?.model ?? "";
    return {
      // Reduced user agents report an empty model.
      model: model === "" ? "unknown" : model,
      platform: [hints?.platform ?? navigator.platform, values?.platformVersion].filter(Boolean).join(" ") || "unknown",
      appVersion,
    };
  } catch {
    return { model: "unknown", platform: navigator.platform || "unknown", appVersion };
  }
}

/**
 * Enrols this tablet (ADR-0028 §2): a new P-256 key the browser never exports, proof of it over the code, then the
 * device, its PIN key (non-extractable HMAC key), its settings and the clock offset are kept in IndexedDB. Nothing is
 * stored unless the server accepted the tablet.
 */
export async function enrollTablet(db: KarryOfflineDb, input: EnrollInput, info: DeviceInfo, send: typeof fetch = fetch): Promise<DeviceRecord | EnrollFailure> {
  const keys = await generateDeviceKey();
  const publicKey = await publicJwk(keys.publicKey);
  let response: Response;
  try {
    response = await send(enrollPath, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        tenant: input.company.trim().toLowerCase(), code: input.code.trim(), publicKey,
        proof: await enrollmentProof(keys.privateKey, input.code.trim()), name: input.name.trim(), ...info,
      }),
    });
  } catch {
    return "offline";
  }

  if (!response.ok) {
    const code = await problemCode(response);
    return code === "devices.enrollment_expired" ? "expired"
      : code === "devices.enrollment_unknown" ? "unknown"
      : response.status === 400 ? "invalid"
      : "failed";
  }

  const enrolled = (await response.json()) as Enrolled;
  const device: DeviceRecord = {
    id: "this",
    deviceId: enrolled.deviceId,
    tenantId: enrolled.tenantId,
    siteId: enrolled.siteId,
    privateKey: keys.privateKey,
    publicKey,
    pinKey: await importPinKey(fromBase64(enrolled.pinKey) as Uint8Array<ArrayBuffer>),
    enrolledAt: enrolled.serverTime,
    clockOffsetMs: Date.parse(enrolled.serverTime) - Date.now(),
  };
  await db.transaction("rw", db.device, db.settings, async () => {
    await db.device.put(device);
    await db.settings.put({ id: "this", autoLockMinutes: Number(enrolled.settings.autoLockMinutes), offlineSessionHours: Number(enrolled.settings.offlineSessionHours) });
  });
  return device;
}

async function problemCode(response: Response): Promise<string | null> {
  try {
    return ((await response.json()) as { code?: string }).code ?? null;
  } catch {
    return null;
  }
}
