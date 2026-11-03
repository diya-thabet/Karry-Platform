---
title: "UI/UX Review: Human–Computer Interaction Analysis of the Portal and Field App, Findings and Design Rules"
area: quality
tags: [ux, ui, hci, usability, accessibility, wcag, design-system, nielsen, hick, fitts, gestalt, portal, field-app, rtl]
source: "Owner request 2026-10-29 (after ADR-0030): review the frontend from a design and HCI perspective"
status: live
last_updated: 2026-10-29
---

# UI/UX review

> [Docs index](../README.md) › [05-quality](README.md)

The owner asked on 2026-10-29, before the next sub-phase, for a design review of the frontend: what a person
feels and stumbles on in the portal and the field app, judged against the laws of human–computer interaction (HCI),
then the fixes. This page is the analysis, the list of findings with their status, and the **design rules every new
screen must follow** (§6). It is a live document: a finding is closed with the PR that fixes it.

## 1. Scope and method

- **What was reviewed:** every portal screen as the company administrator of *Carrière Démo B* (home, sites and a
  site, machines and a machine, audit, settings and its eight screens, materials, new site), in light and dark, at a
  desktop width (1366 px) and a phone width (390 px), in French, Arabic, N'Ko and Latin Malinké; the operator console
  and the support banner; the field app's code and its screens in Storybook (enrolment, unlock, home, machine finder).
- **How:** screenshots of the running demo (Playwright against the local stack), a read of every component in
  `@karry/ui` and every page in `apps/portal` and `apps/field`, and a check of each against the laws in §2.
- **Severity** (same scale as [defect severity](defect-severity-and-triage.md), applied to usability):
  **High**: people make mistakes or cannot tell what happened; **Medium**: slows people down or breaks consistency;
  **Low**: polish.

## 2. The laws and heuristics used

| Law / heuristic | What it says | What it means for Karry |
|---|---|---|
| **Hick's law** | Decision time grows with the number and complexity of choices | Show few choices at a time; put rarely used settings one level down (progressive disclosure) |
| **Miller's law / chunking** | People hold about 7 ± 2 items in working memory | Group long lists (menus, forms) into labelled chunks |
| **Fitts's law** | Time to hit a target depends on its distance and size | Big targets for frequent actions; the main action where the eye already is; ≥ 48 px on tablets (gloves) |
| **Jakob's law** | Users expect your product to work like the others they know | Breadcrumbs, account menu top right, "Language ›" submenu, skeletons while loading: the conventions of Google, Microsoft and SAP products |
| **Doherty threshold** | Productivity soars when the system answers in < 400 ms | Show *something* at once (a skeleton), never a blank page or a misleading "empty" box |
| **Tesler's law** (conservation of complexity) | Complexity cannot be removed, only moved | The system carries it (defaults, the company's currency, the site's time zone), not the user |
| **Postel's law** | Be liberal in what you accept | Accept `9,79` and `9.79`, spaces in codes, any case in search |
| **Gestalt: proximity, similarity, common region, alignment** | Things close, alike or in one box are read as one group; aligned edges read as one column | Header and cells of a column share one edge; related fields in one card; one look for one kind of control |
| **Von Restorff (isolation) effect** | The item that differs is remembered | One primary button per view; destructive actions look different (red) and stand apart |
| **Serial-position effect** | First and last items of a list are remembered best | Most used navigation first; sign-out last in the account menu |
| **Goal-gradient / Zeigarnik** | People speed up near a goal and remember unfinished tasks | Show progress in multi-step tasks (import wizard), keep unsaved changes visible |
| **Peak–end rule** | Experiences are judged by their peak and their end | Clear success at the end of a task (toast with what changed), calm errors with a way out |
| **Aesthetic–usability effect** | People find a good-looking interface easier and forgive more | Consistent spacing, alignment and typography are not decoration: they build trust in the data |
| **Nielsen's 10 heuristics** | Visibility of system status; match with the real world; user control and freedom; consistency and standards; error prevention; recognition rather than recall; flexibility; minimalist design; help users recover from errors; help and documentation | Used as the checklist for every finding (H1–H10 below) |
| **WCAG 2.2 AA** (AAA for the outdoor theme) | Perceivable, operable, understandable, robust | Already tested by axe on every story; this review adds what axe cannot see (state announced to screen readers, colour as the only signal, focus order) |

## 3. What is already good (keep it)

- **Accessibility foundation:** Radix primitives (focus traps, keyboard, Escape), axe on every Storybook story in five
  languages, status chips with icon **and** text, a visible thick focus ring, `prefers-reduced-motion` respected.
- **Three themes from one token file** with contrast tested in CI (`theme.test.ts`); the outdoor theme is AAA.
- **Right to left done properly** (ADR-0030): logical CSS only, mirrored drawer and arrows.
- **Permission-driven UI:** people only see the screens and buttons they may use (Nielsen H8, minimalist design).
- **Phone layout of tables as cards**, a collapsible sidebar remembered per browser, a support banner impossible
  to miss during an operator visit.
- **Calm motion** (150–250 ms), `tabular-nums` for figures, error dialogs that give a support reference.

## 4. Findings

Status: ✅ fixed (PR) · ⏳ planned (sub-phase or backlog) · 🚫 won't fix (reason).

### 4.1 Navigation and wayfinding

| ID | Where | Problem | Law / heuristic | Severity | Fix | Status |
|---|---|---|---|---|---|---|
| UX-01 | Account menu | The menu lists **all five languages and all three themes** at once: 13 rows for a choice made once. Scanning it to find "Sign out" takes longer every time a language is added | Hick, Miller, Nielsen H8 | Medium | Two rows that show the current value, **"Language · Français ›"** and **"Theme · Light ›"**, each opening a submenu (owner's request). Sign-out stays last (serial position) | ✅ |
| UX-02 | Account menu | The current language and theme are marked with a ✓ hidden from screen readers: a blind user cannot tell which language is active | WCAG 4.1.2 (name, role, value) | High | Radio menu items (`menuitemradio`, `aria-checked`) | ✅ |
| UX-03 | Account menu | The header says only "Signed in as Name": no e-mail, no company. Someone with two accounts (two companies) cannot tell which one is open | Nielsen H1, H6 | Low | An identity block: initials, name, e-mail | ✅ |
| UX-04 | Sub-pages | Six different "back" patterns: "← Settings", "Back to sites", "All machines", a plain link above the title, none on Users, Roles and Plan | Jakob, Nielsen H4 | Medium | **Breadcrumbs** in the page header on every page below a section (`Settings › Users`), the same component everywhere | ✅ |
| UX-05 | Sidebar | On a settings sub-page, the sidebar highlights "Settings" but nothing says which of the eight screens is open | Nielsen H1 (where am I?) | Low | Solved by the breadcrumbs (UX-04) | ✅ |
| UX-06 | All pages | The page title is hand-built in 13 places (`<h1 className=…>`), with three different gaps and alignments; one page used `PageHeader` | Consistency (H4), DRY | Medium | Every page uses `PageHeader` (breadcrumbs, title, status, description, actions) | ✅ |

### 4.2 Visual hierarchy and Gestalt

| ID | Where | Problem | Law / heuristic | Severity | Fix | Status |
|---|---|---|---|---|---|---|
| UX-07 | Every table | **Column headers are centred while the cells start at the left**: the browser centres `<th>` by default and the table never reset it. The eye cannot tie a header to its column | Gestalt alignment / proximity | High | Headers aligned like their cells (start, or end for numbers); a test reads the computed alignment | ✅ |
| UX-08 | Site, machine, material | "Archive the site" floats between two cards, styled like any secondary button | Von Restorff, Nielsen H5 | Medium | A "danger zone" card at the end of the page, red button | ⏳ backlog |
| UX-09 | Site page | One 3,200 px page: form, shift versions, non-working days and 14 upcoming shifts | Miller (chunking), Nielsen H8 | Medium | In-page tabs or a section index (Details · Shifts · Non-working days) | ⏳ backlog |
| UX-10 | Audit log | Long identifiers wrap on three lines and the date on two; 40 rows fill 5,000 px | Gestalt (rows read as one line), H8 | Medium | Dates on one line; identifiers shortened with the full value in a tooltip | ✅ |
| UX-11 | Phone cards | The card's title is the code (`CAR-KAK`) and the name sits in a line below: people recognise names, not codes | Recognition over recall (H6) | Low | Tables may name which column is the card's title (`cardTitle`); sites, machines and materials use their name | ⏳ backlog |

### 4.3 Feedback and system status

| ID | Where | Problem | Law / heuristic | Severity | Fix | Status |
|---|---|---|---|---|---|---|
| UX-12 | Every list | While a list loads, it shows the **empty state** (dashed box, inbox icon) with the word "Loading…": for a moment the company looks empty | Doherty, Nielsen H1 | High | **Skeleton rows** in `DataTable` (`loading`), announced as busy to screen readers | ✅ |
| UX-13 | Detail pages | A site, machine, material or role page renders **nothing** while it loads (blank page) | Doherty, H1 | Medium | A page skeleton (`PageSkeleton`) | ✅ |
| UX-14 | Home | Counters show "…" while they load | H1, aesthetic–usability | Low | Skeleton figures | ✅ |
| UX-15 | Toasts | Success, warning and error differ only by a coloured left border: no icon, no way to close it | WCAG 1.4.1 (colour alone), H3 | Medium | Status icon, close button, the same left border | ✅ |
| UX-16 | Field app home | The four big actions (fuel, meter, production, report a problem) look ready and do nothing when tapped (they arrive in Phase 2) | H1, error prevention (H5) | Medium | Shown as "Coming soon" and not clickable until their phase ships | ✅ |

### 4.4 Forms and controls

| ID | Where | Problem | Law / heuristic | Severity | Fix | Status |
|---|---|---|---|---|---|---|
| UX-17 | Lists | The "Show archived" check box is the browser's own: different look in every browser, no theme | Consistency (H4), aesthetic–usability | Low | A design-system `Checkbox` (themed, 44 px target, outdoor outline) | ✅ |
| UX-18 | Date fields | Native date inputs show the browser's format (mm/dd/yyyy for a French person on an English browser); not translatable, not right to left | Match with the real world (H2) | Medium | A `DateField` following the language (`intlLocale`) | ⏳ backlog |
| UX-19 | Long forms | The save button is at the end of long forms; after scrolling, people forget to save | Fitts, Zeigarnik | Medium | A sticky action bar when the form has unsaved changes | ⏳ backlog |
| UX-20 | Machine page | "Move" is disabled without saying why (same site chosen) | H1, H9 | Low | A hint under the disabled button | ⏳ backlog |

### 4.5 Empty states

| ID | Where | Problem | Law / heuristic | Severity | Fix | Status |
|---|---|---|---|---|---|---|
| UX-21 | Everywhere | The same inbox icon for "nothing yet", "choose a person on the left", "not found" and (UX-12) "loading" | Recognition (H6), match with the real world (H2) | Low | `EmptyState` takes an icon: a pointer for "choose an item", a search icon for "not found", the inbox only for empty lists | ✅ |

### 4.6 Code health of the frontend (asked in the same request)

| ID | Where | Problem | Principle | Severity | Fix | Status |
|---|---|---|---|---|---|---|
| UX-22 | `eslint.config.js` | The React hooks rules run on the apps only, not on `@karry/ui`, whose components use hooks too | Fail fast, consistent rules | Medium | Hooks rules on every React package | ✅ |
| UX-23 | `apps/portal/src` | Twelve app-wide files (layout, account menu, providers, API, flags, error dialog) mixed at the root with the home page | Single responsibility, package by feature | Low | `shell/` (layout, navigation, account menu, company brand), `app/` (providers, API, flags, errors), `home/` | ✅ |
| UX-24 | `@karry/ui` | Three copies of "read/write a choice in `localStorage`, survive private mode" (theme, language, sidebar) | DRY | Low | One `rememberedChoice` helper, tested | ✅ |
| UX-25 | `Layout.tsx` | One 213-line file with five components (layout, navigation, account menu, placeholder section, not found) | Single responsibility | Low | One component per file in `shell/` | ✅ |
| UX-26 | CI | The guidelines say the field app's bundle budget (250 KB) "is checked in CI"; no step did | Trust the gates | Medium | `tools/ci/bundle_budget.py` (tested) after the build in the `frontend` job | ✅ |
| UX-27 | Tests | Tests read a table as soon as it appeared; with skeleton rows a table appears before its rows | Tests follow behaviour | Low | `findTable` (portal `testing/tables.ts`) waits until the table is no longer busy | ✅ |

## 5. The language and theme menu (UX-01, UX-02, UX-03) in detail

**Before:** one flat menu: signed-in line, *Language* label, 5 languages, *Theme* label, 3 themes, sign-out (13 rows).

**After:**

```text
┌───────────────────────────────┐
│ (DT) Demo TenantAdmin          │  identity block (name, e-mail)
│      tenant-admin@…karry.test  │
├───────────────────────────────┤
│ 🌐 Language       Français  ›  │  submenu: 5 radio items, each in its own language
│ 🎨 Theme          Light     ›  │  submenu: 3 radio items with their icon
├───────────────────────────────┤
│ ⇥  Sign out                    │  last (serial position), apart (separator)
└───────────────────────────────┘
```

- The row shows the **current value** (recognition rather than recall): no need to open the submenu to know it.
- Submenus open to the side with the mouse, with → / Enter / Space on the keyboard (← in right to left), and on tap
  on a phone (Radix `DropdownMenu.Sub`).
- Items are `menuitemradio` with `aria-checked`: a screen reader says "Arabic, not checked, 3 of 5".
- Each language is written in its own language and script (`lang` attribute on the item, so a screen reader reads
  العربية with an Arabic voice).
- The field app keeps its two large selects at the foot of the home screen: on a tablet with gloves a select of
  56 px is easier to hit than a nested menu (Fitts), and the choice is made once per tablet.

## 6. Design rules for every new screen (binding)

Reviewers check these; a breach is a review finding like a failing test.

1. **Page header:** every page uses `PageHeader`; every page below a section has breadcrumbs; one `h1` per page.
2. **One primary action per view** (Von Restorff); destructive actions are `danger` buttons, apart from the others,
   and ask for confirmation when they cannot be undone.
3. **Never blank, never fake-empty:** lists pass `loading` to `DataTable`; pages show `PageSkeleton` until their data
   is known; `EmptyState` is only for "there is nothing" and says what to do next.
4. **Tables:** header and cells share their alignment (start; end for numbers with `numeric`); dates on one line;
   identifiers shortened with the full value available.
5. **Choices:** ≤ 7 visible options at once; more go one level down (submenu, select, search); the current value is
   always visible.
6. **State is announced:** current choices are radio or checkbox roles, never a decorative ✓; status is icon + text,
   never colour alone.
7. **Targets:** ≥ 40 px in the portal, ≥ 48 px (56 px used) in the field app.
8. **Logical CSS only** (`ms-`, `text-start`, …) and directional icons flipped in right to left (ADR-0030).
9. **Copy:** short, in the person's words, no internal codes unless they are what the person types.
10. **Tests:** a new component gets a component test of its behaviour and its a11y story (axe in five languages);
    a fix of a visual rule gets a test that would fail without it (computed style, role, state).

## 7. How the fixes are tested

- **Component tests** (`packages/ui/src/components/ux.test.tsx`, Vitest + Testing Library, 15 tests): menu submenu
  opened by keyboard with radio state and `lang`, breadcrumbs, table skeleton (busy, named, no empty state) and header
  alignment, figure and page skeletons, empty-state icon, check box, toast icon and close, remembered choices with
  storage blocked.
- **Browser tests** (`packages/ui/src/stories.a11y.tsx`, real Chromium): axe on every story (new: header with
  breadcrumbs, page skeleton, loading figure, loading table, check boxes, account menu with submenus) in three themes and
  in Arabic and N'Ko; **header alignment read from the computed style** in French and Arabic.
- **Portal tests:** `App.test.tsx` (three menu rows, theme and language through the submenus, `aria-checked`, each
  language named in its own `lang`), `shell/wayfinding.test.tsx` (breadcrumbs on the eight settings screens and on a
  site, a machine and a material; none on top sections; one `h1`; skeleton table busy on a slow network; page
  skeleton), the field app's actions disabled with "Coming soon".
- **Mutation check (2026-10-29):** header alignment back to the browser default (caught by the unit test and the
  browser test), radio group without its value (caught), no busy flag on a loading table (caught).

## 8. Results (2026-10-29)

| Measure | Before | After |
|---|---|---|
| Rows in the account menu | 13 | 3 (+ the identity block) |
| Ways to go back from a sub-page | 6 | 1 (breadcrumbs) |
| Hand-built page titles | 13 | 0 (`PageHeader` everywhere) |
| Lists that looked empty while loading | 20 | 0 |
| Pages blank while loading | 6 | 0 |
| Field app initial JS (gzip) | 207 KB | 203 KB, now checked in CI |
| New tests | — | 31 (portal 16, design system 15) + 2 browser alignment tests + 4 bundle-budget tests |

## 9. Next steps

- Backlog items UX-08, UX-09, UX-11, UX-18, UX-19, UX-20: one small PR each, before or during Phase 2 (the field
  forms of Phase 2 reuse `DateField` and the sticky action bar).
- A usability test with three real users (an administrator, a site manager, a field operator) on the pilot site,
  with the tasks of the [Phase 1 demo script](../04-phases/phase-01-platform-foundation/plan.md): time on task,
  errors, and the System Usability Scale (SUS ≥ 75 targeted).
- Re-run this review at each phase exit gate.
