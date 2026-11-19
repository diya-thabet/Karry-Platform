import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { type Page } from "@playwright/test";
import { expect, test } from "./guard";
import { enrolledTablet, enrollmentCode, meterNow, portal, startAndEnd } from "./fieldTablet";

const compose = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..", "deploy", "docker", "compose.yaml");

/** Rotates the transit key `karry-server` as the runbook does (karry-server-key-rotation.md): the root token stays in the container. */
function rotateServerKey(): number {
  const output = execFileSync("docker", [
    "compose", "-f", compose, "exec", "-T", "openbao", "sh", "-c",
    'export BAO_ADDR=http://127.0.0.1:8200 BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /openbao/keys/init.txt)"; '
      + "bao write -f transit/keys/karry-server/rotate > /dev/null && bao read -field=latest_version transit/keys/karry-server",
  ], { encoding: "utf8" });
  return Number(output.trim());
}

/**
 * How many of the `count` newest received envelopes the portal's audit trail shows "Vérifiée". The page is opened anew
 * each time: applying the same filters again shows the list already loaded, without the envelopes received since.
 */
async function verifiedEnvelopes(page: Page, count: number): Promise<number> {
  await page.goto(`${portal("demo-carriere-b")}/audit`);
  await page.getByLabel("Événement").fill("trust.envelope_received.v1");
  await page.getByRole("button", { name: "Appliquer les filtres" }).click();
  let verified = 0;
  for (let index = 0; index < count; index++) {
    await page.getByRole("button", { name: /^Détails de l’entrée n° / }).nth(index).click();
    // The verdict loads after the details open: wait for it a little, then count it.
    if (await page.getByText("Vérifiée", { exact: true }).waitFor({ timeout: 5_000 }).then(() => true, () => false)) {
      verified++;
    }

    await page.keyboard.press("Escape");
  }

  return verified;
}

test.describe("signatures across a server key rotation (P2.3 carried to P2.5, ADR-0033 §6)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-SIG-004: envelopes countersigned before a rotation of karry-server stay verified, and the new ones are verified with the new key", async ({ browser, page }) => {
    test.setTimeout(420_000);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, await enrollmentCode(page));
    const outbox = tablet.getByRole("region", { name: "Envoi des saisies" });

    // Before: a start and an end, sent and countersigned (the job runs every 30 seconds in Karry.Workers).
    await startAndEnd(tablet, meterNow());
    await expect(outbox.getByText("Tout est envoyé")).toBeVisible({ timeout: 180_000 });
    await expect.poll(() => verifiedEnvelopes(page, 2), { timeout: 120_000, intervals: [5_000] }).toBe(2);

    const version = rotateServerKey();
    expect(version).toBeGreaterThan(1);

    // After: another shift, countersigned with the new key version; the two before are still verified.
    await startAndEnd(tablet, meterNow());
    await tablet.getByRole("button", { name: "Actualiser" }).click();
    await expect(outbox.getByText("Tout est envoyé")).toBeVisible({ timeout: 180_000 });
    await expect.poll(() => verifiedEnvelopes(page, 4), { timeout: 120_000, intervals: [5_000] }).toBe(4);
    await context.close();
  });
});
