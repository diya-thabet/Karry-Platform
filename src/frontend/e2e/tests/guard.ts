import { expect, test as base } from "@playwright/test";

// Every browser test runs the production build under the production page policy (src/frontend/security-headers.ts,
// hardening plan H-W2). A blocked script, style, image, worker or request on one of our pages fails the test, so a
// change that the policy would break in production is caught here.
export const test = base.extend<{ policyViolations: string[] }>({
  policyViolations: [
    async ({ page }, use) => {
      const violations: string[] = [];
      page.on("console", (message) => {
        const where = message.location().url;
        const ours = /^https?:\/\/([a-z0-9-]+\.)?localhost:517[34]\//.test(where);
        if (ours && message.text().includes("Content Security Policy")) {
          violations.push(`${where}: ${message.text()}`);
        }
      });
      await use(violations);
      expect(violations, "the page policy blocked something on our pages").toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
