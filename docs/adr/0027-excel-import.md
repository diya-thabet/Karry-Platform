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

4. **The flow is upload → validate → preview → commit, run as a job in `Karry.Workers`.**
   - `POST /api/v1/imports` (multipart, `kind`) stores the file and creates a job (`validating`).
     `GET /api/v1/imports/{id}` gives the state, progress (rows done / total), counts and the issues (paged).
     `POST /{id}/commit` asks for the commit (`committing`); `POST /{id}/discard` drops a job that is not committed.
   - The jobs are rows in `imports.jobs`. A worker in `Karry.Workers` claims the next job with
     `FOR UPDATE SKIP LOCKED`, so several workers never run the same job and a job survives a restart (it is claimed
     again). Quartz stays for scheduled work; it runs in memory today, so it is not a queue.
   - Validation never writes business data. It records the issues and, per row, the planned action
     (`create`, `update`, `unchanged`), which is the preview.
   - **The commit validates again inside the transaction** (the data may have changed since the preview). Any error
     rolls the whole file back and the job returns to `validated` with the new issues. Nothing is committed while one
     error remains (TC-P1-ORG-001).
   - The commit runs in one transaction of the owning module's database context. Entity changes, their outbox rows
     and their ledger entries commit or roll back together (`ModuleDbContext` joins the open transaction).
   - A job that stops half-way (worker killed) is claimed again; natural keys make the repeat harmless.

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
   - **Malware scanning** is a port (`IFileScanner`). Production uses a ClamAV service before validation; locally and
     in tests a scanner that accepts everything is registered, and the job records which scanner ran.

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
   - The job's life is in the company's ledger: `imports.import_uploaded.v1` (kind, file name, size, SHA-256, rows),
     `import_validated`, `import_committed` (created, updated, unchanged), `import_discarded`, `import_file_deleted`.
     Created entities are ledgered by their own modules.

10. **Permissions.** `imports.files.manage` (Tenant Admin) uploads, reads, commits and discards. A target also
    requires its own permission: the job runs with the requester's current access, rebuilt when the job runs, so
    rights removed after the upload stop the commit. An asset row on a site the requester does not manage is an error
    on that row.

## Options considered

- **Validation in the browser (SheetJS or exceljs).** Rejected: the rules live on the server and would be duplicated;
  exceljs is unmaintained (technology stack).
- **One file with a sheet per kind, committed together.** Rejected for P1: a transaction across module schemas
  would couple the modules, and onboarding is done kind by kind anyway (sites before assets). It can be offered later
  as a sequence of single-kind jobs.
- **Synchronous import in the request.** Rejected: progress could not be shown and a slow file would hit request
  timeouts.
- **Quartz persistent store as the job queue.** Rejected: it is not configured today and one table with
  `SKIP LOCKED` is simpler to reason about and to test.
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
