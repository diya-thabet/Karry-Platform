---
title: "ADR-0027: Excel import as a server-side job: one kind per file, targets owned by their modules, all-or-nothing commit"
area: adr
tags: [import, excel, closedxml, onboarding, background-jobs, object-storage, retention, formula-injection, idempotency]
source: "Phase 1 sub-phase P1.14"
status: proposed
last_updated: 2026-10-27
---

# ADR-0027 — Excel import as a server-side job: one kind per file, targets owned by their modules, all-or-nothing commit

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (P1.14 in progress)
- **Date:** 2026-10-27
- **Deciders:** Product owner, software architect
- **Related:** US-P1-ORG-03 (AC1–AC5); TC-P1-ORG-001, TC-P1-ORG-002, TC-P1-ORG-004;
  [ADR-0018](0018-background-jobs-and-dispatcher.md) (background work in `Karry.Workers`);
  [ADR-0026](0026-asset-registry-meters-and-labels.md) (assets, meters, object storage);
  [ADR-0025](0025-units-engine-and-materials.md) (materials); [ADR-0024](0024-sites-geometry-and-shift-time.md) (sites);
  [technology stack §7.1.1](../02-architecture/technology-stack.md#711-verified-versions-phase-1-start-2026-10-22) (ClosedXML)

## Context

A new customer arrives with their fleet, materials, sites and people in spreadsheets. US-P1-ORG-03 asks for templates,
a validation report with cell references, a preview, an all-or-nothing commit that can be repeated without duplicates,
1,000 assets in under a minute, an audited job and the original file kept for 90 days. The questions:

1. **Who knows the rules of a row.** Each module already validates its own data (asset codes, densities, site
   geometry). Copying those rules into an import module would let them drift.
2. **What "all or nothing" covers** when a file could touch several modules, and when users also need Keycloak
   accounts, which are outside our database.
3. **Where the work runs.** A 1,000-row file must not hold an HTTP request open, and progress must be visible.
4. **What an uploaded file can do to us.** An `.xlsx` is a zip of XML: zip bombs, macros, external links, formulas.
5. **How long the file lives, and who can see it.**

## Decision

1. **One kind per file.** A file imports exactly one of: `sites`, `assets`, `meters` (initial readings of existing
   assets), `materials`, `users`. Each kind has its own template. One file, one owning module, so one database
   transaction can hold the whole commit.

2. **Import targets live in the module that owns the data.**
   - `Karry.BuildingBlocks/Imports` defines the contract: `IImportTarget` (kind, columns, required permission,
     `ValidateAsync(rows)` → per-row issues and planned action, `CommitAsync(rows)`), the column model and the
     issue model (`code`, `column`, cell reference such as `C14`, `error` or `warning`).
   - Each module implements its target with its own operations (`AssetOperations.RegisterAsync` and so on), so every
     created entity is checked by the same rules and recorded in the ledger by the same events as through the API.
   - A new `Imports` module owns the jobs, the files, the reports and the endpoints. It finds targets through
     dependency injection and never references another module's application layer.

3. **Natural keys make an import repeatable** (AC3, TC-P1-ORG-002).
   - Keys: site code, asset code, (asset code, meter kind), material code, user employee ID (field) or e-mail (office).
   - A row whose key exists is compared with the stored data: identical → `unchanged`; different → `update` of the
     columns the template carries (an asset on another site is moved, keeping its site history). A key that appears
     twice in a file is an error on both rows.
   - Importing the same file twice therefore creates nothing the second time.
   - *As built (slice 3, assets and meters):* the targets use the module's domain rules and aggregates directly (the
     same `AssetRules`, `Asset.Register` / `Change` / `MoveTo` and `Meter.Record` as the API, so the same events and
     ledger entries), with the whole file loaded by code in one query rather than row by row through the operations.
     Shared row codes (`imports.value_required`, `number_invalid`, `date_invalid`, `key_duplicate`, `data_changed`) and
     `ImportIssue.Error` live in BuildingBlocks. The plan limit is checked once for the file's creations
     (`ILimitGuard.CheckCreateAsync(metric, count)`). A save that hits a concurrent change (unique code, row version)
     writes nothing and reports `imports.data_changed` on row 0.

4. **The flow is upload → validate → preview → commit, run as a job in `Karry.Workers`.**
   - `POST /api/v1/imports?kind=&fileName=` (the body is the `.xlsx`, `Idempotency-Key`) checks the file, stores it and
     creates a job (`validating`, 202). `GET /api/v1/imports/{id}` gives the state and counts;
     `GET /{id}/issues` the problems with their cells and `GET /{id}/rows` the preview (both paged).
     `POST /{id}/commit` asks for the commit (`committing`); `POST /{id}/discard` drops a job that is not committed.
     The state is the progress: a client polls the job while it is `validating` or `committing`.
   - The jobs are rows in `imports.jobs` (with their issues and preview in `imports.issues` and `imports.rows`). The
     steps are queued by the job's own events through the module's **outbox** (ADR-0018): `import_uploaded` starts the
     validation and `import_commit_requested` the commit. The dispatcher in `Karry.Workers` claims them with
     `FOR UPDATE SKIP LOCKED`, runs them in the job's tenant, retries a step that fails and claims it again after a
     restart; so several workers never run the same step and nothing needs a second queue. A step that finds its job
     already past that state does nothing. Quartz stays for scheduled work (retention).
   - Validation never writes business data. It records the issues and, per row, the planned action
     (`create`, `update`, `unchanged`), which is the preview.
   - **The commit validates again inside the transaction** (the data may have changed since the preview). Any error
     rolls the whole file back and the job returns to `validated` with the new issues. Nothing is committed while one
     error remains (TC-P1-ORG-001).
   - The commit runs in one transaction of the owning module's database context. Entity changes, their outbox rows
     and their ledger entries commit or roll back together (`ModuleDbContext` joins the open transaction). The job's
     result is saved afterwards in the Imports transaction of the outbox step.
   - A step that stops half-way (worker killed) is claimed again; natural keys make the repeat harmless (a commit
     repeated after the data was written finds every row `unchanged`).
   - Every problem is recorded as an issue on row 0 (the file) rather than thrown: kind no longer allowed
     (`imports.kind_forbidden`), file deleted by retention (`imports.file_deleted`), refused by the scanner
     (`imports.file_infected`), unreadable or too large once opened. So the same file always gives the same report.

5. **Users are imported in two steps.** The commit creates the Karry users and their invitations in one transaction.
   Their Keycloak accounts and invitation messages are created afterwards by an outbox handler, retried until done,
   because Keycloak is not part of our transaction. An imported user is `invited` until then, as through the API.

6. **The file is checked before anything reads it** (security).
   - **Format:** `.xlsx` only, at most 10 MB, recognised by its content (a zip with
     `[Content_Types].xml` and `xl/workbook.xml`), not by its name. `.xls`, `.xlsm`, `.csv` and anything else: 400
     `imports.file_type_unsupported`.
   - **Active content is refused:** a workbook with macros (`vbaProject.bin`), ActiveX, embedded objects or external
     links is refused (`imports.file_active_content`). Formulas are allowed; their cached values are read and never
     evaluated, and the cell gets a warning.
   - **Zip bombs:** the entries' declared sizes are summed before opening (at most 100 MB uncompressed, ratio at most
     100 to 1), and the reader stops at 5,000 data rows and 60 columns.
   - The type, size, active-content and zip-bomb checks run at upload (400, nothing stored); the cells are read by the
     worker.
   - **Malware scanning** is a port (`IFileScanner`), run by the worker before the file is read. Production uses a
     ClamAV service; where none is configured `NoFileScanner` accepts every file, and the job and its
     `import_validated` ledger entry record which scanner ran (`none`).

7. **Formula injection is neutralised wherever we write a spreadsheet** (templates now, exports later).
   `SpreadsheetText.Neutralise` prefixes a cell that starts with `=`, `+`, `-`, `@`, a tab or a carriage return with an
   apostrophe, and every writer goes through it. In an `.xlsx`, ClosedXML stores that apostrophe as Excel's quote
   prefix: the cell keeps the text and Excel shows it as text, never as a formula. Imported text is stored as typed;
   the neutralisation happens on the way out.

8. **Templates are generated by the API with ClosedXML** (AC1): `GET /api/v1/imports/templates/{kind}?language=fr|en`.
   A data sheet with headers in the chosen language and one example row, and a hidden sheet of lists for the
   drop-downs (types, statuses, units, and the company's own site codes). Headers are accepted in French or English,
   in any order. Unknown columns give a warning; a missing required column gives an error on row 1.

9. **Retention and audit** (AC5).
   - The original is stored under `tenants/{tenant}/imports/{job}/original.xlsx` in the `Storage` bucket, with its
     SHA-256 on the job.
   - A daily job deletes originals older than 90 days and records it. Production also sets a bucket lifecycle rule
     as a second line.
   - The job's life is in the company's ledger: `imports.import_uploaded.v1` (kind, file name, size, SHA-256, actor),
     `import_validated` (rows, errors, warnings, scanner), `import_commit_requested` (actor), `import_committed`
     (created, updated, unchanged), `import_discarded`, `import_file_deleted`. Created entities are ledgered by their
     own modules.
   - Only the file's name is kept (a path such as `C:\fakepath\flotte.xlsx` becomes `flotte.xlsx`).

10. **Permissions.** `imports.files.manage` (Tenant Admin) uploads, reads, commits and discards. A target also
    requires its own permission, checked at upload and at the commit request. Each step runs with the access of the
    person who caused it (the uploader for the validation, whoever asked for the commit for the commit), rebuilt when
    the step runs, so rights removed in between stop it. `Karry.Workers` therefore knows every module's permission
    declarations (`WorkerModules`). An asset row on a site the requester does not manage is an error on that row.

## Options considered

- **Validation in the browser (SheetJS or exceljs).** Rejected: the rules live on the server and would be duplicated;
  exceljs is unmaintained (technology stack).
- **One file with a sheet per kind, committed together.** Rejected for P1: a transaction across module schemas
  would couple the modules, and onboarding is done kind by kind anyway (sites before assets). It can be offered later
  as a sequence of single-kind jobs.
- **Synchronous import in the request.** Rejected: progress could not be shown and a slow file would hit request
  timeouts.
- **Quartz persistent store as the job queue.** Rejected: it is not configured today.
- **A job table claimed by its own worker loop (`SKIP LOCKED`).** Rejected during P1.14: row-level security hides the
  jobs of every company from a worker that has not chosen one, so the loop would need a table outside RLS or a
  tenant-by-tenant scan. The outbox already claims, retries and sets the tenant, and is tested for crashes.
- **Rows imported one by one, keeping the good ones.** Rejected by AC3: a half-imported fleet is harder to fix than
  a refused file.

## Consequences

- ✅ An imported asset is exactly as valid as one entered on screen, and is in the ledger the same way.
- ✅ Re-importing a corrected file is safe; the customer can work by trial and error.
- ✅ A crash during a commit leaves either the whole file or nothing.
- ⚠ A user import is complete in Karry before the Keycloak accounts exist; the screen shows users as invited until the
  outbox has created them.
- ⚠ Production needs a ClamAV service and a bucket lifecycle rule (operations doc); until then only the structural
  checks run.
- ⚠ A file is limited to 5,000 rows; larger fleets are split.
- ⚠ A step that keeps failing (database down) is retried by the outbox and then dead-lettered; its job stays
  `validating` or `committing` and shows in the outbox lag alert.
