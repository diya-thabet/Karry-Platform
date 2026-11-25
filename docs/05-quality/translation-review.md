---
title: "Translation Review: Status of Every Language and How Native Speakers Review the Drafts"
area: quality
tags: [i18n, l10n, translation, review, arabic, malinke, maninka, nko, rtl, catalogs, native-speaker, glossary]
source: "ADR-0030 (owner decisions 2026-10-29)"
status: live
last_updated: 2026-11-24
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
   message. CI runs `python3 tools/i18n/nko_from_latin.py --check`: every N'Ko message must be the spelling of its
   `emk.json` message, so the two scripts never say different things. A message the N'Ko reader corrected is listed
   in `tools/i18n/nko-reviewed.txt` (one key per line; `--check` prints the keys to add) and is left alone from then
   on. A Malinké message changed later makes its N'Ko message fail the check again: remove it from `nqo.json`, run
   `--missing`, and have the reader look at it.
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
| Signature (handwritten) | Signature | التوقيع | bolonɔ | "hand mark"; also used for the tablet's signature |

## 5. Waiting for review (keys added or changed after the drafts)

New or changed messages after 2026-10-29 are listed here by the person who adds them (ADR-0030 Consequences), so a
reviewer knows what to read. Remove a key once a native speaker has checked it in every draft language.

| Date | Keys | Languages to check | Why |
|---|---|---|---|
| 2026-10-29 | all keys | ar, emk, nqo | First drafts |
| 2026-10-29 | `portal.nav.breadcrumbs`, `field.action.soon` | ar, emk, nqo | UI/UX review (breadcrumbs, "coming soon" on the field actions) |
| 2026-11-03 | `errors.request.rate_limited`, `errors.request.too_large`, `field.sync.state.busy` | ar, emk, nqo | P1.18a request limits (429, 413, a busy server on the tablet) |
| 2026-11-03 | `errors.server.unavailable` | ar, emk, nqo | P1.18a resilience (503: a service of the API briefly down) |
| 2026-11-04 | `portal.conflict.title`, `portal.conflict.body`, `portal.conflict.keepMine`, `portal.conflict.showTheirs` | ar, emk, nqo | P1.18a record versions (someone else saved while you edited) |
| 2026-11-04 | `errors.request.version_conflict` | ar, emk, nqo | P1.18a record versions (two saves at the same moment, e.g. density versions of one material) |
| 2026-11-09 | `common.signature.*` (9 keys: title, statement, instructions, area, clear, confirm, `problem.tooFewStrokes`, `problem.tooFewPoints`, `problem.tooSmall`) | ar, emk, nqo | P2.3 signature pad: "I confirm the values above" is a legal statement, check it first |
| 2026-11-09 | `permissions.fieldops.entries.read` | ar, emk, nqo | P2.4 read permission of shift entries ("saisies de poste" in French) |
| 2026-11-19 | `permissions.fieldops.checklists.read`, `permissions.fieldops.checklists.manage` | ar, emk, nqo | P2.6 checklist permissions ("check-lists de démarrage" in French) |
| 2026-11-19 | `portal.checklists.*`, `portal.settings.checklistsHint` | ar, emk, nqo | P2.6 checklist builder in the portal ("check-list de démarrage", "point", "critique" in French) |
| 2026-11-19 | `permissions.fieldops.holds.release` | ar, emk, nqo | P2.6 release of a safety hold ("lever l’immobilisation" in French) |
| 2026-11-19 | `permissions.integrity.flags.read` | ar, emk, nqo | P2.7 read permission of integrity flags ("alertes d’intégrité" in French) |
| 2026-11-19 | `portal.meterEvents.*` (31 keys) | ar, emk, nqo | P2.7 meter events in the portal: replaced, reset, failed, repaired; "Pourquoi", "Photos" |
| 2026-11-19 | `field.estimated`, `field.start.failedMeter`, `field.end.failedMeter`, `field.end.operatingTime`, `field.end.distance`, `field.end.operatingHelp`, `field.end.estimatedEnd` | ar, emk, nqo | P2.7 failed-meter mode on the tablet ("temps de marche", "estimé") |
| 2026-11-19 | `portal.holds.*` (25 keys) | ar, emk, nqo | P2.6 portal list of held machines and the release dialog ("lever l’immobilisation" in French) |
| 2026-11-24 | `portal.reviewPolicies.*` (27 keys), `portal.settings.reviewPoliciesHint` | ar, emk, nqo | P2.8 review policies page ("double validation", "escalader") |
| 2026-11-23 | `permissions.verification.policies.manage` | ar, emk, nqo | P2.8 review policies permission ("politiques de vérification") |
| 2026-11-23 | `permissions.verification.items.second-approve` | ar, emk, nqo | P2.8 second approver of a large correction |
| 2026-11-23 | `field.history.status.approved`, `field.history.status.refused`, `field.history.reason.*` (6), `field.history.comment` | ar, emk, nqo | P2.8 a controller's decision on the tablet ("validée", "rejetée par le contrôleur") |
| 2026-11-23 | `portal.users.fieldAccount*` (5 keys) | ar, emk, nqo | P2.8 office ↔ field account link ("compte terrain") |
| 2026-11-23 | `permissions.verification.items.review` | ar, emk, nqo | P2.8 approve/reject permission ("valider ou rejeter") |
| 2026-11-23 | `permissions.verification.items.read` | ar, emk, nqo | P2.8 review queue permission ("saisies à vérifier") |
| 2026-11-25 | `portal.integrity.rule.RULE-FO-08.*`, `portal.integrity.flag.RULE-FO-08`, `portal.integrity.threshold.graceMinutes` | ar, emk, nqo | P2.9 RULE-FO-08 missing end (date and time of the shift's end, minutes allowed); "fin manquante" |
| 2026-11-25 | `portal.integrity.settle.*`, `portal.review.unlinkedFieldAccount`, `errors.integrity.*` (30 keys) | ar, emk, nqo | P2.9 settling a continuity gap (reasons, proposal, approval), the R-15 banner; "régler"/"règlement" for settle, "latigɛ" |
| 2026-11-24 | `portal.review.keys.*`, `portal.review.select*`, `portal.review.approve*Many/Selected` (14 keys) | ar, emk, nqo | P2.9 keyboard shortcuts overlay and bulk approval (ICU plurals: Arabic gives `one` and `other`, a native reviewer may add `two`/`few`/`many`) |
| 2026-11-24 | `portal.review.detail.*`, `portal.review.reason.*`, `portal.review.open*`, `errors.verification.*` (47 keys) | ar, emk, nqo | P2.9 review panel: approve, reject with a reason, correct (meter values and fuel), second approval, and the refusals in words (separation of duties); "refuser", "corriger", "seconde approbation" |
| 2026-11-24 | `portal.nav.review`, `portal.review.*` (31 keys) | ar, emk, nqo | P2.9 review queue: entries waiting for a decision, the entry flags in words (as the tablet's history says them), due and overdue; "contrôle" for the section, "alerte" for a flag; Arabic arrow mirrored (←) |
| 2026-11-24 | `portal.integrity.rule.RULE-FO-11.*`, `portal.integrity.flag.RULE-FO-11`, `portal.integrity.flags.settled`, `permissions.integrity.flags.settle` | ar, emk, nqo | P2.8 RULE-FO-11 (fraud suspected: a gap settled as unexplained or unsettled beyond 48 h) and the settled close reasons (ICU `select`); "réglé", "latigɛ" |
| 2026-11-20 | `portal.integrity.rule.RULE-FO-10.*`, `portal.integrity.flag.RULE-FO-10` | ar, emk, nqo | P2.7 RULE-FO-10: start without the pre-start checklist; "contrôle avant démarrage" as on the tablet |
| 2026-11-20 | `portal.integrity.rule.RULE-FO-09.*`, `portal.integrity.flag.RULE-FO-09` | ar, emk, nqo | P2.7 RULE-FO-09: who signed, with the reason (ICU `select` with a nested date); "signataire" |
| 2026-11-20 | `portal.integrity.flags.*`, `portal.integrity.flag.*` (32 keys) | ar, emk, nqo | P2.7 integrity flag list: what each rule found with its values (ICU `select` for start/end, ahead/behind; plurals), "alertes d’intégrité", "lasɔmini" |
| 2026-11-19 | `portal.integrity.*` (31 keys), `portal.settings.integrityHint`, `permissions.integrity.rules.manage` | ar, emk, nqo | P2.7 integrity rules page: what each rule checks, severities, thresholds with units ("alerte", "seuil", "tolérance" in French; "lasɔmini" in Malinké as for the flags) |
| 2026-11-19 | `field.checklist.*` (15 keys), `field.start.block.unsafe`, `field.history.flag.unsafe` | ar, emk, nqo | P2.6 checklist step on the tablet: "held as unsafe" and "Not OK" are what operators read before a start |
| 2026-11-09 | `portal.signature.*` (17 keys: the verification badge, its checks and results) | ar, emk, nqo | P2.3 portal "Verified" badge |
| 2026-11-09 | `field.camera.*` (10 keys: viewfinder, focus help, take, retake, keep, preview, cancel, failures) | ar, emk, nqo | P2.5 in-app camera for meter photos |
| 2026-11-18 | `field.start.*` (33 keys: the start-of-shift screen, its blocks, summary and restored draft), `field.machines.choose`; `field.action.meter` removed | ar, emk, nqo | P2.5 start of shift: "below the previous end" and "machine in use" are the messages operators will read most |
| 2026-11-18 | `field.end.*` (25 keys: end of shift, Δ, stoppage reasons, blocks), `field.active.label` | ar, emk, nqo | P2.5 end of shift: the stoppage reasons are a fixed list until the owner confirms the codes |
| 2026-11-18 | `field.history.*` (20 keys: my history, statuses, the flags in words) | ar, emk, nqo | P2.5 my history: the flag sentences tell an operator what the controller will check |

## 6. Not translated yet

The Keycloak sign-in pages (realm locales `fr`, `en`), e-mails, Excel import templates and PDFs are French and English
only. They get Arabic and Malinké once the reviewed catalogs exist (ADR-0030 §5).
