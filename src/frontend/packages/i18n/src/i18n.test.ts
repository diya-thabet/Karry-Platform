import { checkCatalogs, findUsedKeys } from "./check";
import { catalogs } from "./catalogs";
import { createI18n } from "./setup";
import { intlLocale, languageDirection, resolveLanguage, supportedLanguages } from "./languages";
import { pseudoLocalize } from "./pseudo";

describe("language resolution (US-P1-OFF-06 AC1)", () => {
  it.each([
    [{ user: "en", tenant: "fr", browser: ["fr-FR"] }, "en"],
    [{ user: null, tenant: "en", browser: ["fr-FR"] }, "en"],
    [{ tenant: "de", browser: ["de-DE", "en-US"] }, "en"],
    [{ browser: ["fr-GN"] }, "fr"],
    [{ browser: ["ar-MA", "fr-FR"] }, "ar"],
    [{ tenant: "emk" }, "emk"],
    [{ user: "nqo", tenant: "fr" }, "nqo"],
    [{ user: "bm", browser: ["de-DE"] }, "fr"],
    [{}, "fr"],
  ] as const)("%o resolves to %s (user, then company, then browser, then French)", (input, expected) => {
    expect(resolveLanguage(input)).toBe(expected);
  });
});

describe("writing direction and formats", () => {
  it("writes Arabic and N'Ko right to left, the others left to right", () => {
    expect(supportedLanguages.map((language) => [language, languageDirection(language)])).toEqual([
      ["fr", "ltr"], ["en", "ltr"], ["ar", "rtl"], ["emk", "ltr"], ["nqo", "rtl"],
    ]);
  });

  it("formats Malinké in Latin script like Guinea's administration (browsers have no data for it), the others as themselves", () => {
    const day = Date.UTC(2026, 9, 29);
    const long = (language: string) => new Intl.DateTimeFormat(intlLocale(language), { dateStyle: "long", timeZone: "UTC" }).format(day);

    expect(intlLocale("emk")).toBe("fr-GN");
    expect(long("emk")).toBe("29 octobre 2026");
    expect(intlLocale("nqo")).toBe("nqo");
    expect(new Intl.NumberFormat(intlLocale("nqo")).format(15)).toBe("߁߅"); // N'Ko digits
    expect(intlLocale("ar")).toBe("ar");
  });

  it("sets the page's lang and dir with the language, and follows a switch", async () => {
    const page = { lang: "", dir: "" }; // these tests run in Node: the page is a stand-in
    vi.stubGlobal("document", { documentElement: page });
    try {
      const i18n = await createI18n({ language: "fr" });
      expect(page).toEqual({ lang: "fr", dir: "ltr" });

      await i18n.changeLanguage("ar");
      expect(page).toEqual({ lang: "ar", dir: "rtl" });
      await i18n.changeLanguage("emk");
      expect(page).toEqual({ lang: "emk", dir: "ltr" });
      await i18n.changeLanguage("nqo");
      expect(page).toEqual({ lang: "nqo", dir: "rtl" });
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

describe("catalogs (TC-P1-OFF-007)", () => {
  it("are complete, valid ICU and use the same placeholders in every language", () => {
    expect(Object.keys(catalogs)).toEqual([...supportedLanguages]);
    expect(checkCatalogs(catalogs)).toEqual([]);
  });

  it("name every language in that language, the same in every catalog", () => {
    for (const messages of Object.values(catalogs)) {
      expect(supportedLanguages.map((language) => messages[`common.language.${language}`])).toEqual(["Français", "English", "العربية", "Maninkakan", "ߒߞߏ"]);
    }
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

  it("uses the six plural forms of Arabic", async () => {
    const i18n = await createI18n({ language: "ar" });

    expect([0, 1, 2, 3, 11, 100].map((count) => i18n.t("field.sync.pending", { count }))).toEqual([
      "أُرسل كل شيء",
      "إدخال واحد بانتظار الإرسال",
      "إدخالان بانتظار الإرسال",
      "3 إدخالات بانتظار الإرسال",
      "11 إدخالًا بانتظار الإرسال",
      "100 إدخال بانتظار الإرسال",
    ]);
  });

  it("loads only the start language, and another one when it is chosen", async () => {
    const i18n = await createI18n({ language: "fr" });

    expect(supportedLanguages.filter((language) => i18n.hasResourceBundle(language, "translation"))).toEqual(["fr"]);
    await i18n.changeLanguage("nqo");
    expect(supportedLanguages.filter((language) => i18n.hasResourceBundle(language, "translation"))).toEqual(["fr", "nqo"]);
    expect(i18n.t("common.language")).toBe(catalogs.nqo["common.language"]);
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
