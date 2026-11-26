---
title: "ADR-0040: Documents and public verification"
area: adr
tags: [adr, phase-2, p2-11, documents, pdf, verification, qr, signatures]
source: "P2.11 planning (sub-phases.md §P2.11, decision table), US-P2-SIG-05, US-P2-SIG-06, ADR-0007, ADR-0033, P1.9 masking"
status: proposed
last_updated: 2026-11-26
---

# ADR-0040: Documents and public verification

> [Docs index](../README.md) › [ADRs](README.md) · [P2.11 slice plan](../04-phases/phase-02-field-operations/p2.11-shift-report-plan.md)

- **Status:** Proposed (2026-11-26, P2.11 planning)
- **Date:** 2026-11-26

## Context

A shift's entries are signed on the tablet (ADR-0007), countersigned by the server (ADR-0033) and reviewed by a
controller (ADR-0037). Outside the company, people still ask for a paper: a client, an auditor, an inspector. They
need a report they can print, and a way to know it was not edited after Karry issued it, without an account. The
sub-phase plan reserved this ADR for the PDF engine (after a time-boxed spike) and the public verification page.

## Decision

### 1. A PDF engine with a permissive licence, chosen by a spike

The engine must render 200 entries with images and embedded fonts in under 10 s (US-P2-SIG-05 AC3) under a licence
the product can ship. **PDFsharp + MigraDoc 6.2.4**, the last MIT release (6.5.0 became AGPL-3.0-only; the licence is
read from the package, not the web page), is the candidate. QuestPDF's Community licence ends at USD 1 M revenue and
needs the owner's approval, so it is only the fallback. Slice 1 measures the candidate on the generated 200-entry
shift; this section records the result. Fonts are embedded (an open-licence font in the repository), never the
system's.

**Spike result (slice 1, 2026-11-26, [spikes/pdf-engine](../../spikes/pdf-engine/README.md)):** PDFsharp + MigraDoc
6.2.4 is chosen. The licence is MIT, read from the `PDFsharp-MigraDoc` and `PDFsharp` 6.2.4 packages (6.5.0's says
AGPL-3.0-only). The 200-entry shift, each entry with its own signature image, renders in 0.6–0.8 s on the first run of
the process (15 A4 landscape pages, 302 KB), far under the 10 s budget. Noto Sans Regular and Bold (SIL OFL 1.1, in
`src/backend/Modules/Documents/Fonts`) are embedded as TrueType subsets through an `IFontResolver` registered once
per process. Its transitive `System.Security.Cryptography.Pkcs` 8.0.1 has two advisories and is pinned to 10.0.13,
as the product already pins it. QuestPDF is not needed, so no licence decision is asked of the owner.

### 2. A Documents module that reads through contracts

A module `Documents` (schema `documents`, RLS) builds the shift report from the other modules' contracts: entries and
their Δ (FieldOps), decisions and the controller (Verification), flags (Integrity), signature strokes (Trust), site
and shift (Organization), names (Identity). It never reads their tables. The report is masked for its reader with
the same decision as the API and exports (P1.9 `SensitiveExport`): a column the reader's role may not see is absent,
not blanked (TC-P2-DOC-003).

### 3. The document envelope: what is signed and ledgered

A report's content has a canonical form (RFC 8785) and its hash (`sha256:`), printed in the footer. A PDF cannot
contain the hash of its own bytes, so the envelope keeps two hashes: the content's and the finished PDF's. Both are
signed with the server key (ADR-0033) and ledgered (`documents.shift_report_issued.v1`) with the envelope ID, the
shift, the site and who issued it. Issuing the same shift again makes a new envelope; the earlier ones stay valid
for what they say.

The ledger entry is written when the document is issued, in the same transaction as the envelope: from then on the
hashes cannot change unseen (the ledger is hash-chained and anchored). The server's signature needs the signing
service (ADR-0033 §3), which may be down or, locally, absent: as for countersignatures (ADR-0033 §4), the document is
issued at once and the Workers sign its envelope when the service answers. The public page answers "genuine" when the
token and the hashes match a ledgered envelope, and shows the signature and its key version once it exists. Trust
exposes the signer to other modules through a contract (`Trust.Contracts`), so Documents never reaches into Trust's
application layer.

The QR code is drawn by **QRCoder 1.8.1** (MIT, read from the package; published 2026-10-13; 1.9.0 was under the
seven-day rule when chosen) with its PNG writer, which needs no `System.Drawing` call.

### 4. Public verification: an unguessable token, a fragment, one answer for every miss

The QR opens `/verify/{envelopeId}#{token}` on the platform address (the `verify.` domain later). The token is 128
random bits, stored hashed; it travels in the URL fragment, so it never reaches a server log or a proxy, and the page
posts it in a request body. Every miss (unknown envelope, wrong token, another company) gets the same "not found"
answer after the same work (the token hash is compared in constant time even when the envelope does not exist), and
the endpoint is rate-limited per address (TC-P2-SIG-005). The answer shows the document type, date, issuing company,
status and hashes; people's names only if the company's policy allows (US-P2-SIG-06 AC1). A visitor can drop the PDF
on the page: it is hashed in the browser and compared with the signed hash (TC-P2-DOC-002), the file never leaves the
device.

### 5. A strict page policy for the public page

The page needs no sign-in, no third-party script and no frame: `default-src 'self'`, `frame-ancestors 'none'`, and
the API answers it with `Cache-Control: no-store`.

## As built

As built (slice 2a, the read contracts): the modules that own the report's data answer through their contracts, in the
current company (RLS): FieldOps `IShiftEntriesOfShift.OfShiftAsync(site, date, shift)` (the current version of each
entry: machine, operator, meter kind, start and end, status, flags, envelope, corrector; at most 2000), Verification
`IReviewOutcomes.OfRecordsAsync` (each record's newest reviewed version, its status and the last decision: who by
sign-in subject, when, the reason code; an escalation is not a decision), Trust `IEnvelopeImages.ImageHashesAsync` (the
handwritten signature image hash read from each envelope as signed; the image comes from the evidence store), Identity
`IUserNames.ByIdAsync` (names by user ID, field people included). Machine codes come from Assets' `IAssetDirectory`.

As built (slice 2b, the Documents module): `Karry.Modules.Documents` (Contracts, Domain, Application, Infrastructure,
Api, Tests), registered in the Host and the Workers. `ShiftReportContents.OfShiftAsync(site, date, shift)` checks the
reader's scope on the site (`documents.shift-reports.read`, site-scoped, office roles and operators; another site gets
the same "not found" as an unknown one), reads the entries, review outcomes, names (by ID for operators, by subject for
controllers), machine codes and signature image hashes in one call per contract, refuses a shift above 2000 entries
rather than printing it short, and masks the table with `SensitiveExport`. Rows are site-scoped (`ISiteScoped`), so a
reader's field access on that site applies: found by the TC-P2-DOC-003 test, where a site manager first lost a column
they may read. `ShiftReportContent` is the table as the reader sees it (column codes, rows of text); its canonical form
(RFC 8785) and `sha256:` hash are what the footer prints and the envelope will sign. **Masking today:** no module
declares a sensitive field (shift entries carry no money, threat model P2.4), so the report has no masked column yet;
TC-P2-DOC-003 proves the wiring with a test-only sensitive field, and any field declared later is masked in the report
without further work.

As built (slice 3a, the renderer): `ShiftReportPdf.Build` makes the MigraDoc document (checked as DDL in tests) and
`Render` the bytes: A4 landscape, a header with the company, site, date and shift, the reader's table exactly as the
content has it (an absent column stays absent), the signature images by their evidence hash, and a footer with the
content hash, the verification address without its token (the token is in the QR only) and the QR. Column titles in
French or English; Arabic and N'Ko fall back to French until their fonts and right-to-left layout are added. Fonts are
embedded resources of the Documents infrastructure, resolved by one `IFontResolver` per process. **Found by the
tests:** PDFsharp's core build cannot read 1-bit grey PNG (QRCoder's own writer) and MigraDoc then prints an error text
in place of the image without failing; the QR is therefore drawn from QRCoder's module matrix as an 8-bit grey PNG, and
a test checks the PDF really contains the images. Tablet signatures are 8-bit RGBA (supported).

As built (slice 3b, issuing): `GET /api/v1/documents/shift-reports?siteId&date&shift&language`
(`documents.shift-reports.read`) issues the reader's report: the content (slice 2b), the signature images from the
evidence store under the tablet that signed each entry (`EvidenceKeys.For(company, tablet, hash)`; an image the store
cannot give leaves its cell empty, never the report undone), a new envelope ID and token (128 random bits, base64url),
the PDF with the QR to `{Documents:VerifyOrigin}/verify/{envelopeId}#{token}`, then the envelope (`documents.envelopes`,
RLS: site, shift, content hash, the SHA-256 of exactly the returned bytes, the token's SHA-256, who issued it, when)
saved with its ledger entry `documents.shift_report_issued.v1` (both hashes) in one transaction. The answer is the PDF,
`Cache-Control: no-store`, its envelope in `X-Karry-Envelope`. Each call is a new envelope. The signature columns stay
empty until the Workers sign (slice 3c).

As built (slice 3c, signing): Trust's contract `IDigestSigner.TrySignAsync(sha256Digest)` answers a `DigestSignature`
(key ID, base64 signature) or null when the signing service (OpenBao transit) is not configured or does not answer;
`ServerDigestSigner` wraps the server signer. The Workers job `document-signing` (every minute) signs, company by
company and 100 at a time, the envelopes not yet signed, oldest first. The signed digest is the SHA-256 of
`{envelopeId}|{contentHash}|{pdfHash}`, so the signature covers which document, what it says and its exact bytes. An
envelope is signed once (`DocumentEnvelope.Sign`), and the signature is ledgered (`documents.envelope_signed.v1`) in
the same transaction. When the service does not answer, the run stops and the next minute tries again; nothing issued
waits on it.

As built (slice 4, public verification API): `POST /api/v1/documents/verify/{envelopeId}` (anonymous, no company,
`Cache-Control: no-store`) with `{ token, pdfHash? }` in the body. The envelope's company and token hash are also kept
in `documents.verification_keys` (envelope ID, company, token SHA-256), written with the envelope in the same
transaction and declared global in `deploy/database/global-reference-tables.txt`: the page knows no company, and the
envelopes stay under RLS. `DocumentVerification` reads that key once, hashes the token (capped at 64 characters) and
compares it in constant time, against a stand-in hash when the envelope does not exist; only a match reads the
envelope, the company name (Tenancy) and the site (Organization) in a scope of its own set to that company. Every
miss (malformed ID, unknown envelope, wrong or missing token, a caller of another company) is the same 404
`documents.not_found`. The answer: type, company, site code and name, shift date and code, issue time, content and
PDF hashes, the server's signature once the Workers made it (key ID, value, time), and `pdfMatches` when the visitor
sent a PDF hash (`sha256:` or bare hex, any case). No person's name and no company ID: a company setting to show names
(US-P2-SIG-06 AC1) is a follow-up. The per-address limit for anonymous callers (`RateLimits:AnonymousPerAddressPerMinute`,
60) applies. Envelopes issued before this slice have no key (local data only).

## Options considered

1. **Sign the PDF itself (PAdES)** — Pros: readers check it in their PDF viewer. Cons: a qualified certificate per
   company, and viewers disagree; the page answers anyone with a phone. Kept for a market that requires it.
2. **The token in the path or query** — Cons: it ends in server and proxy logs and in browser history servers.
3. **Fragment token, server-side check (chosen)** — Pros: no log holds the token; a lost QR exposes one document.

## Consequences

- **Positive:** a printed report is checked by anyone in seconds; a changed byte shows; the report respects masking.
- **Negative:** the public page reveals that a document exists to whoever holds its QR; names follow the company's
  policy.
- **Follow-ups:** PAdES and the `verify.` domain with the deployment; other documents (weighing tickets, invoices)
  reuse the envelope.
