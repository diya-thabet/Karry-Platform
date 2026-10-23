import { checkCatalogs, findUsedKeys } from "./check";
import { catalogs, createI18n } from "./setup";
import { resolveLanguage } from "./languages";
import { pseudoLocalize } from "./pseudo";

describe("language resolution (US-P1-OFF-06 AC1)", () => {
  it.each([
    [{ user: "en", tenant: "fr", browser: ["fr-FR"] }, "en"],
    [{ user: null, tenant: "en", browser: ["fr-FR"] }, "en"],
    [{ tenant: "de", browser: ["de-DE", "en-US"] }, "en"],
    [{ browser: ["fr-GN"] }, "fr"],
    [{ user: "ar", browser: ["ar-SA"] }, "fr"],
    [{}, "fr"],
  ] as const)("%o resolves to %s (user, then company, then browser, then French)", (input, expected) => {
    expect(resolveLanguage(input)).toBe(expected);
  });
});

describe("catalogs (TC-P1-OFF-007)", () => {
  it("are complete, valid ICU and use the same placeholders in French and English", () => {
    expect(checkCatalogs(catalogs)).toEqual([]);
  });

  it.each([
    [{ fr: { a: "x" }, en: {} }, 'en: missing key "a"'],
    [{ fr: { a: " " }, en: { a: "x" } }, 'fr: empty message "a"'],
    [{ fr: { a: "{count, plural, one {#}" }, en: { a: "x" } }, 'fr: invalid ICU message "a"'],
    [{ fr: { a: "Bonjour {name}" }, en: { a: "Hello {company}" } }, '"a": placeholders differ'],
    [{ fr: { a: "{n, plural, one {# {unit}} other {#}}" }, en: { a: "{n, plural, one {#} other {#}}" } }, '"a": placeholders differ'],
  ])("reports broken catalogs %#", (broken, problem) => {
    expect(checkCatalogs(broken).join("\n")).toContain(problem);
  });

  it("reports keys used in source but missing from the catalogs", () => {
    const used = findUsedKeys(`t("portal.nav.home"); t('portal.nav.ghost'); t( "common.close" ); other("x.y")`);

    expect(used).toEqual(["portal.nav.home", "portal.nav.ghost", "common.close"]);
    expect(checkCatalogs(catalogs, used)).toEqual(['source uses unknown key "portal.nav.ghost"']);
  });
});

describe("translation", () => {
  it("formats ICU plurals per language and switches language without a reload", async () => {
    const i18n = await createI18n({ language: "fr" });

    expect(i18n.t("field.sync.pending", { count: 0 })).toBe("Tout est envoyé");
    expect(i18n.t("field.sync.pending", { count: 2 })).toBe("2 saisies en attente d’envoi");
    await i18n.changeLanguage("en");
    expect(i18n.t("field.sync.pending", { count: 1 })).toBe("1 entry waiting to be sent");
    expect(i18n.t("portal.home.welcome", { company: "Carrière A" })).toBe("Welcome to Carrière A");
  });

  it("never falls back to another language: a missing key shows the key", async () => {
    const i18n = await createI18n({ language: "en" });

    expect(i18n.t("does.not.exist")).toBe("does.not.exist");
  });

  it("pseudo-localizes text but keeps ICU arguments working", async () => {
    const i18n = await createI18n({ language: "en", pseudo: true });

    const text = i18n.t("portal.home.welcome", { company: "Acme" });
    expect(text.startsWith("⟦Ŵéļçöɱé ţö Acme")).toBe(true);
    expect(text.endsWith("⟧")).toBe(true);
    expect(pseudoLocalize("{count, plural, one {# item} other {# items}}")).toContain("{count, plural, one {# item} other {# items}}");
  });
});
