import { createHmac } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page } from "@playwright/test";

// Second factors enrolled by the tests (Keycloak's `totpSecret`), kept between local runs: once an account has
// enrolled, every later sign-in asks for a code. Never committed (.gitignore); CI starts from a fresh stack.
const store = join(dirname(fileURLToPath(import.meta.url)), "..", ".auth", "totp.json");

interface Factor {
  secret: string;
  /** The last 30-second window whose code was used (Keycloak refuses a code twice, also across runs). */
  last: number;
}

function factors(): Record<string, Factor> {
  return existsSync(store) ? (JSON.parse(readFileSync(store, "utf8")) as Record<string, Factor>) : {};
}

function remember(username: string, factor: Factor) {
  mkdirSync(dirname(store), { recursive: true });
  writeFileSync(store, JSON.stringify({ ...factors(), [username]: factor }, null, 2));
}

/** RFC 6238 as the realm's OTP policy says (HMAC-SHA1, 6 digits, 30 s); Keycloak keys it with the secret's UTF-8 bytes. */
function totp(secret: string, counter: number): string {
  const message = Buffer.alloc(8);
  message.writeBigUInt64BE(BigInt(counter));
  const hash = createHmac("sha1", Buffer.from(secret, "utf8")).update(message).digest();
  const offset = (hash.at(-1) ?? 0) & 0x0f;
  return String((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

/**
 * A code Keycloak accepts once: a code is never used twice in its 30-second window, so a second sign-in of the same
 * account takes the next window (the realm looks one window ahead), or waits for it.
 */
async function code(username: string, secret: string): Promise<string> {
  const now = Math.floor(Date.now() / 30_000);
  const counter = Math.max(now, (factors()[username]?.last ?? 0) + 1);
  if (counter > now + 1) {
    await new Promise((resolve) => setTimeout(resolve, (counter - 1) * 30_000 - Date.now() + 1_000));
  }

  remember(username, { secret, last: counter });
  return totp(secret, counter);
}

/**
 * After the password: enrols the authenticator at the first sign-in (and keeps its secret), or answers the code
 * asked on later ones. Does nothing when no second factor is asked.
 */
export async function secondFactor(page: Page, username: string) {
  const enrolment = page.locator("input[name=totpSecret]");
  const asked = page.locator("#otp");
  await expect(enrolment.or(asked).or(page.locator("#root"))).toBeAttached();
  if (await enrolment.count() > 0) {
    const secret = await enrolment.inputValue();
    await page.locator("#totp").fill(await code(username, secret));
    await page.locator("#userLabel").fill("E2E");
    await page.locator("#saveTOTPBtn").click();
    return;
  }

  if (await asked.count() > 0) {
    const secret = factors()[username]?.secret;
    if (!secret) {
      throw new Error(`${username} has a second factor this run does not know: reset the local stack or delete its OTP in Keycloak.`);
    }

    await asked.fill(await code(username, secret));
    await page.locator("#kc-login").click();
  }
}
