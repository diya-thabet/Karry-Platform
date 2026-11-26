import { readFile } from "node:fs/promises";
import { portal, signIn } from "./fieldTablet";
import { expect, test } from "./guard";
import { qrAddressOf } from "./pdfQr";

test.describe("the signed shift report and its public verification (P2.11, ADR-0040)", () => {
  test("demo step 5, TC-P2-DOC-001, TC-P2-DOC-002: the site manager downloads the shift report; its QR opens “Verified” without sign-in; a changed PDF is not genuine", async ({ browser, page }) => {
    test.setTimeout(180_000);

    // The site manager downloads today's report of the first shift on the board.
    await page.goto(`${portal("demo-carriere-b")}/review/board`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    const button = page.getByRole("button", { name: "Rapport de poste (PDF)" }).first();
    await expect(button).toBeVisible({ timeout: 30_000 });
    const [download] = await Promise.all([page.waitForEvent("download"), button.click()]);
    expect(download.suggestedFilename()).toMatch(/^shift-report-\d{4}-\d{2}-\d{2}-.+\.pdf$/);
    const path = await download.path();
    const pdf = await readFile(path);
    expect(pdf.subarray(0, 5).toString("latin1")).toBe("%PDF-");

    // A visitor scans the QR: a fresh browser, never signed in, opens the address it holds.
    const address = qrAddressOf(pdf);
    expect(address).toMatch(/^http:\/\/localhost:5173\/verify\/[0-9a-f-]{36}#[A-Za-z0-9_-]{22}$/);
    const visitor = await browser.newContext({ locale: "fr-FR" });
    const check = await visitor.newPage();
    const posted: string[] = [];
    check.on("request", (request) => { posted.push(request.url()); });
    await check.goto(address);

    await expect(check.getByText("Document authentique")).toBeVisible();
    await expect(check.getByText("Vérifié", { exact: true })).toBeVisible();
    await expect(check.getByText("Rapport de poste", { exact: true })).toBeVisible();
    await expect(check.locator("#username")).toHaveCount(0); // no sign-in page on the way
    const token = new URL(address).hash.slice(1);
    expect(posted.filter((url) => url.includes(token))).toEqual([]); // the token never travels in a URL

    // The PDF as issued matches; the same PDF with one changed byte is not genuine (hashed in the browser).
    const input = check.locator("input[type=file]");
    await input.setInputFiles(path);
    await expect(check.getByText("Ce PDF est le document émis")).toBeVisible();
    const changed = Buffer.from(pdf);
    const middle = Math.floor(changed.length / 2);
    changed.writeUInt8(changed.readUInt8(middle) ^ 0x01, middle);
    await input.setInputFiles({ name: "rapport.pdf", mimeType: "application/pdf", buffer: changed });
    await expect(check.getByText("Non authentique : ce PDF diffère du document émis")).toBeVisible();

    // A wrong token is "not found", like any other miss (TC-P2-SIG-005).
    await check.goto(address.replace(/#.*/, "#not-the-token"));
    await expect(check.getByText("Document introuvable")).toBeVisible();
    await visitor.close();
  });
});
