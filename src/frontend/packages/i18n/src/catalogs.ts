// Every catalog at once, for the catalog check and the tests. The apps never import this module: they load the
// active language's catalog only (setup.ts), so the other languages stay out of the first download.
import ar from "./catalogs/ar.json" with { type: "json" };
import emk from "./catalogs/emk.json" with { type: "json" };
import en from "./catalogs/en.json" with { type: "json" };
import fr from "./catalogs/fr.json" with { type: "json" };
import nqo from "./catalogs/nqo.json" with { type: "json" };
import type { Language } from "./languages";

export const catalogs: Record<Language, Record<string, string>> = { fr, en, ar, emk, nqo };
