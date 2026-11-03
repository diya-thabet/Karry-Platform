---
title: "Translation Review: Status of Every Language and How Native Speakers Review the Drafts"
area: quality
tags: [i18n, l10n, translation, review, arabic, malinke, maninka, nko, rtl, catalogs, native-speaker, glossary]
source: "ADR-0030 (owner decisions 2026-10-29)"
status: live
last_updated: 2026-11-03
---

# Translation review

> [Docs index](../README.md) › [05-quality](README.md)

The apps speak five languages ([ADR-0030](../adr/0030-arabic-and-malinke-languages.md)). French and English are
written by the team; **Arabic and both Malinké catalogs are drafts** written by the agent on 2026-10-29 and must be
reviewed by native speakers before customers rely on them (owner decision). This page is the review's single list.

## 1. Status

| Language | Catalog | Written by | Confidence | Status | Reviewer |
|---|---|---|---|---|---|
| French | `fr.json` | Team | High | ✅ In use | Product owner |
| English | `en.json` | Team | High | ✅ In use | Product owner |
| Arabic (Modern Standard) | `ar.json` | Agent draft, 2026-10-29 | Medium: correct MSA; quarry and maintenance terms to confirm | ⏳ Waiting for review | *wanted* |
| Malinké, Latin script | `emk.json` | Agent draft, 2026-10-29 | **Low**: simple sentences, French technical loanwords, spelling closer to Bambara in places | ⏳ Waiting for a rewrite | *wanted* |
| Malinké, N'Ko script | `nqo.json` | Generated from `emk.json` (`tools/i18n/nko_from_latin.py`) | **Low**: inherits the Latin draft, **no tone marks**, borrowed sounds guessed | ⏳ Waiting for a rewrite | *wanted* |

Files: `src/frontend/packages/i18n/src/catalogs/`. Every catalog holds the same 1,044 keys (2026-11-03: three keys for request limits added after the UI/UX review).

## 2. Who is needed

- **Arabic:** one native reader who knows quarries or construction (Maghreb or Gulf both fine: the text is Modern
  Standard Arabic). About a day for the whole catalog.
- **Malinké:** one native speaker from Upper Guinea who writes both Latin Malinké and N'Ko (N'Ko teachers and
  translators are common in Kankan). About three days: Latin first, then N'Ko from the corrected Latin, with tones.
  Ask them to choose between a loanword (*masin*, *kamiyɔn*, *faktiri*) and a Malinké word for each glossary term below
  and keep the choice everywhere.

## 3. How a reviewer works

1. Open the portal and the field app in the language (account menu › language; field app: language switcher) on the
   demo data and read every screen; the catalog file is the second step, for what the screens did not show
   (errors, empty states).
2. Correct messages **directly in the catalog file**. Keep everything inside `{…}` exactly as it is: `{name}`,
   `{count, plural, one {…} other {…}}` (translate only the words inside the inner braces), `#`, `{at, date, medium}`.
   Keep codes and examples in Latin letters: `PEL-01`, `Africa/Tunis`, `#RRGGBB`, `carriere-sfax`, `CSV`, `PIN`.
3. Arabic plurals: use the forms the sentence needs among `zero`, `one`, `two`, `few` (3–10), `many` (11–99), `other`;
   `other` is required.
4. Run `pnpm --filter @karry/i18n check:catalogs` (from `src/frontend`): it fails on a missing key, broken ICU or a
   changed placeholder.
5. N'Ko: once `emk.json` is corrected, regenerate the N'Ko draft once with
   `python3 tools/i18n/nko_from_latin.py --force`, then add the tones and fix the spelling by hand in `nqo.json`.
   From then on **never use `--force` again**: new messages are added with `--missing`, which keeps every corrected
   message.
6. Mark the language ✅ in §1 with the reviewer's name and the date, and remove its keys from §5.

## 4. Glossary (keep these consistent)

| English | French | Arabic (draft) | Malinké Latin (draft) | Note |
|---|---|---|---|---|
| Company | Entreprise | الشركة | sosiyete | loanword |
| Site | Site | الموقع | baarayɔrɔ | "work place" |
| Machine | Engin | الآلة | masin | loanword |
| Material | Matériau | المادة | fɛn | "thing": to replace with a precise term |
| Shift | Poste | الوردية | kart | loanword (*quart*), to confirm |
| Meter (hour meter) | Compteur | العداد | jatebaga | "counter" |
| Tablet | Tablette | الجهاز اللوحي | tabileti | loanword |
| Role / permission | Rôle / droit | الدور / الصلاحية | jɔyɔrɔ / yamaruya | |
| Administrator | Administrateur | المسؤول | ɲɛmɔɔ | "leader" |
| Audit log | Journal d'audit | سجل التدقيق | lajɛli sɛbɛn | |
| Integrity check | Contrôle d'intégrité | فحص السلامة | tilennenya lajɛli | |
| Support code | Code support | رمز الدعم | dɛmɛ kodi | |
| Subscription / plan | Abonnement / offre | الاشتراك / الباقة | abɔnɛman / plan | loanwords |
| Invoice | Facture | الفاتورة | faktiri | loanword |
| Density (bank / loose / compacted) | Densité (en place / foisonnée / compactée) | الكثافة (في الموضع / مفككة / مدموكة) | girinya (a yɔrɔ la / a sɔlɔlen / a digilen) | to confirm with a quarry engineer |
| Quarry | Carrière | مقلع | kabakurun bɔyɔrɔ | |

## 5. Waiting for review (keys added or changed after the drafts)

New or changed messages after 2026-10-29 are listed here by the person who adds them (ADR-0030 Consequences), so a
reviewer knows what to read. Remove a key once a native speaker has checked it in every draft language.

| Date | Keys | Languages to check | Why |
|---|---|---|---|
| 2026-10-29 | all keys | ar, emk, nqo | First drafts |
| 2026-10-29 | `portal.nav.breadcrumbs`, `field.action.soon` | ar, emk, nqo | UI/UX review (breadcrumbs, "coming soon" on the field actions) |
| 2026-11-03 | `errors.request.rate_limited`, `errors.request.too_large`, `field.sync.state.busy` | ar, emk, nqo | P1.18a request limits (429, 413, a busy server on the tablet) |
| 2026-11-03 | `errors.server.unavailable` | ar, emk, nqo | P1.18a resilience (503: a service of the API briefly down) |

## 6. Not translated yet

The Keycloak sign-in pages (realm locales `fr`, `en`), e-mails, Excel import templates and PDFs are French and English
only. They get Arabic and Malinké once the reviewed catalogs exist (ADR-0030 §5).
