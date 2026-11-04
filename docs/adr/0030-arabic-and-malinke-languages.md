---
title: "ADR-0030: Arabic and Malinké (Latin and N'Ko scripts) in the apps, right to left included"
area: adr
tags: [i18n, l10n, languages, arabic, malinke, maninka, nko, rtl, bidi, fonts, intl, translation, catalogs]
source: "Owner request 2026-10-29 (after P1.16)"
status: accepted
last_updated: 2026-10-29
---

# ADR-0030 — Arabic and Malinké (Latin and N'Ko scripts) in the apps, right to left included

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner decisions 2026-10-29; translations are drafts, see Consequences)
- **Date:** 2026-10-29
- **Deciders:** Product owner, software architect
- **Related:** US-P1-OFF-06 (language choice), TC-P1-OFF-006 (accessibility of every story), TC-P1-OFF-007
  (catalogs); [ADR-0020](0020-frontend-toolchain-versions.md) (i18next + ICU), [non-functional requirements](../02-architecture/non-functional-requirements.md)
  (i18n row), [translation review](../05-quality/translation-review.md)

## Context

The apps shipped in French and English, with "Arabic RTL-ready" as an architecture promise and native Arabic planned for
Phase 11. The owner asked on 2026-10-29 for two more languages in the frontend now, and answered three questions:

1. **Malinké in both scripts**, as two separate languages: Latin and N'Ko. Malinké (Maninka) is spoken across Guinea,
   Mali and neighbouring countries; N'Ko is its own alphabet, written right to left, widely read in Guinea.
2. **Arabic as Modern Standard Arabic** (one written standard for every Arabic-speaking customer).
3. **Translations drafted by the agent and marked for review** by native speakers before customers see them.

Problems to solve: two right-to-left scripts in a layout written left to right; fonts the field app must have offline;
browsers that have no formatting data for Malinké; and keeping two Malinké catalogs from drifting apart.

## Decision

### 1. Language codes

| Code | Language | Script | Direction | Dates and numbers (`Intl`) |
|---|---|---|---|---|
| `fr` | French | Latin | ltr | `fr` |
| `en` | English | Latin | ltr | `en` |
| `ar` | Modern Standard Arabic | Arabic | **rtl** | `ar` (Latin digits, as in North and West Africa) |
| `emk` | Malinké (Eastern Maninkakan, ISO 639-3) | Latin | ltr | **`fr-GN`**: browsers have no `emk` data and would fall back to American English, so Malinké takes the formats of Guinea's administration |
| `nqo` | Malinké in N'Ko | N'Ko | **rtl** | `nqo` (CLDR: N'Ko digits) |

`nqo` is the code browsers and CLDR use for the N'Ko written standard, which is how Malinké is written in N'Ko. The
codes live in one list, `supportedLanguages` of `@karry/i18n`, mirrored by `TenantSettings.Languages` (a company may
choose any of them as its default language). Every catalog names each language in that language (Français, English,
العربية, Maninkakan, ߒߞߏ).

### 2. Right to left: the document follows the language, the layout uses logical properties

- The i18n instance sets `<html lang dir>` itself, at start and on every language change (`applyDocumentLanguage`),
  so no screen sets it by hand and a switch mirrors the page at once, without reload.
- Radix menus, selects and dialogs get the direction from `LanguageDirection` (`@radix-ui/react-direction`), which
  sets which arrow key moves where and which side a menu aligns to.
- CSS uses logical properties only: `ms-`/`me-`, `ps-`/`pe-`, `start-`/`end-`, `text-start`/`text-end`,
  `border-s`/`border-e`, `rounded-s`/`rounded-e`. Physical ones (`ml-`, `left-`, `text-right`) are a review finding.
  Directional icons (arrows) flip with `rtl:-scale-x-100`; the mobile drawer slides in from the right.
- Numbers in tables align to the end of the line (left in Arabic and N'Ko), as Arabic and N'Ko readers expect.

### 3. Fonts bundled, loaded only when the script is on screen

Inter has no Arabic or N'Ko letters. The design system adds **Noto Sans Arabic** (variable) and **Noto Sans NKo**
(Fontsource, OFL, bundled, no CDN: the field app works offline). The browser downloads a face only when the page shows
its script (`unicode-range`), so French and English pages load nothing more. The field app's service worker precaches
them (about 270 KB, measured on the field build of 2026-10-29) so a tablet can switch language offline.

### 4. Catalogs and the N'Ko draft

- `ar.json`, `emk.json` and `nqo.json` sit beside `fr.json` and `en.json`; the catalog check (TC-P1-OFF-007) now
  requires every key, valid ICU and the same placeholders in all five. Arabic messages use its six plural forms.
- **One chunk per language, loaded when first shown** (an i18next backend over `import()`): five catalogs bundled
  together would add about 55 KB gzip to every first download and push the field app over its 250 KB budget
  (measured 297 KB). Now the field app's first JS is 207 KB gzip plus one catalog (17–20 KB); the service worker
  precaches all five, so a switch still works offline. `changeLanguage` resolves once the catalog is there.
- `nqo.json` is **spelled from `emk.json`** by `tools/i18n/nko_from_latin.py`: ICU syntax, placeholders, codes,
  examples and acronyms are kept; words are spelled letter by letter in N'Ko; digits become N'Ko digits. Latin
  Malinké spelling does not mark tones and N'Ko does, so the generated file is a starting draft that a native reader
  corrects directly. The script refuses to overwrite an existing `nqo.json`: `--missing` adds new keys and keeps the
  rest, `--force` replaces everything (only while it is still the untouched draft). `--check` (CI, tooling job)
  fails on a N'Ko message that is not the spelling of its Malinké message, except the keys a native reader corrected
  (`tools/i18n/nko-reviewed.txt`): N'Ko messages written by hand had started to say something else than the Latin
  ones (five keys, found and respelled on 2026-11-04).

### 5. Scope of this change

The portal and the field app. Not yet: the Keycloak sign-in pages (realm locales `fr`, `en`; the apps send the language
as `ui_locales`, Keycloak falls back to its default), e-mails, Excel import templates (French and English) and PDFs.
Each gets the languages when the translations are reviewed (see Consequences).

## Options considered

- **Write `nqo.json` by hand, independently of `emk.json`:** two Malinké catalogs that drift apart with every change,
  with a reviewer needed for each. Rejected for the transliteration plus review.
- **A per-script Malinké catalog with `emk-Latn` / `emk-Nkoo` tags:** precise, but region and script subtags break the
  simple `lang` resolution (`fr-GN` → `fr`) and `Intl` knows `nqo`, not `emk-Nkoo`. Rejected.
- **Mirror the layout with a second stylesheet or `[dir=rtl]` overrides:** two layouts to maintain. Logical properties
  make one layout serve both directions. Chosen.
- **Load fonts from Google Fonts:** smaller bundle, but the field app must work offline and the portal must not call a
  third party for every page. Rejected.

## Consequences

- ✅ A person can choose Arabic or Malinké in either script from the account menu (portal) or the language switcher
  (field app); the page mirrors at once; a company can make one of them its default.
- ✅ Every design-system story passes axe in Arabic and N'Ko in a real browser; a switch of direction is tested in the
  portal; the mirrored drawer and table alignment are tested (mutation-checked).
- ⚠ **The three new catalogs are drafts.** Arabic is careful Modern Standard Arabic but needs a native review for the
  quarry vocabulary. **The Malinké drafts are low-confidence:** written by the agent with French technical loanwords,
  without tones in N'Ko; they need a native Malinké speaker to rewrite, not only to proofread. Until reviewed, demos may
  show them, customers should not depend on them. Status and checklist: [translation review](../05-quality/translation-review.md).
- ⚠ Keycloak pages, e-mails, Excel templates and PDFs stay French/English until the reviewed translations exist.
- ⚠ Every new message needs five translations (the catalog check fails otherwise). The agent drafts all five with
  the message (`python3 tools/i18n/nko_from_latin.py --missing` spells only the new Malinké messages into
  `nqo.json` and keeps every corrected one), and lists the new keys under "Waiting for review" in the
  [translation review](../05-quality/translation-review.md), so a reviewer sees exactly what changed.
