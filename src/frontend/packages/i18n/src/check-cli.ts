// CI: `pnpm --filter @karry/i18n check:catalogs`: catalogs consistent, and every key used by the UI exists.
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { checkCatalogs, findUsedKeys } from "./check.ts";
import en from "./catalogs/en.json" with { type: "json" };
import fr from "./catalogs/fr.json" with { type: "json" };

const root = new URL("../../../", import.meta.url).pathname;
// User-facing text lives in the apps and the design system only.
const sources = ["apps", "packages/ui"].flatMap((folder) => files(join(root, folder)));

function files(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (name === "node_modules" || name === "dist" || name === "storybook-static") return [];
    if (statSync(path).isDirectory()) return files(path);
    return /\.(ts|tsx)$/.test(name) && !/\.(test|stories)\.tsx?$/.test(name) ? [path] : [];
  });
}

const used = sources.flatMap((path) => findUsedKeys(readFileSync(path, "utf8")));
const problems = checkCatalogs({ fr, en }, used);
for (const problem of problems) console.error(`✗ ${problem}`);
const count = (n: number) => String(n);
console.log(`${count(Object.keys(fr).length)} keys in fr and en, ${count(new Set(used).size)} used in ${count(sources.length)} source files: ${count(problems.length)} problem(s)`);
process.exit(problems.length === 0 ? 0 : 1);
