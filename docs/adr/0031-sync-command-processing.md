---
title: "ADR-0031: Sync command processing: handlers in the upload request, one savepoint per command"
area: adr
tags: [sync, offline, commands, handlers, savepoint, sequence, gaps, clock-offset, idempotency, phase-2]
source: "Phase 2 sub-phase P2.0 (decision for P2.1)"
status: accepted
last_updated: 2026-11-09
---

# ADR-0031 — Sync command processing: handlers in the upload request, one savepoint per command

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (2026-11-09, built in P2.1; proposed in P2.0)
- **Date:** 2026-11-09
- **Deciders:** Product owner, software architect
- **Related:** US-P2-OFF-03 (AC1–AC4), US-P2-OFF-04 AC1, US-P2-OFF-05 (AC1–AC2); TC-P2-SYNC-001, 003, 008, 009, 010,
  013; threats T-02, T-03, T-05, T-12 ([threat model](../02-architecture/threat-model.md));
  [ADR-0005](0005-offline-first-pwa.md) (offline first), [ADR-0006](0006-immutable-facts.md) (immutable facts),
  [ADR-0015](0015-transactional-outbox.md) (outbox), [ADR-0018](0018-background-jobs-and-dispatcher.md) (outbox dispatcher and contract handlers),
  [ADR-0028 §6](0028-field-devices-offline-data-shared-tablet.md#6-uploads-preparing-phase-2) (uploads as built in P1),
  [offline sync §12.3](../02-architecture/offline-sync.md#123-conflict-policy) (conflict policy),
  [Phase 2 sub-phases §3](../04-phases/phase-02-field-operations/sub-phases.md#33-decisions-to-record-adrs)

## Context

Since P1.15 a tablet uploads signed commands (`POST /api/v1/sync/batches`). The server checks each signature, keeps
the tablet's gap-free sequence and stores the exact signed text in the append-only `sync.inbox`, but it does nothing
else: Phase 1 had no command types. From Phase 2 every field fact (a shift started, a meter read, a checklist
submitted, a signature given) is a command, and the modules that own those facts (FieldOps, Assets, Trust and later
Weighbridge, Fuel, Inventory) must act on them.

The questions:

1. **When** is a command handled: in the upload request, or later by a background worker?
2. **How** does a handler in another module write its rows atomically with Sync's bookkeeping, when each module has
   its own `DbContext` and schema?
3. **What can go wrong** with a fact, and what is the answer: rejected (the tablet keeps it and shows an error) or
   stored and questioned?
4. What happens to a command that arrives **before an earlier one** (a gap in the sequence), and to a command type
   **this server does not know** (a newer app)?
5. How does the server learn **how far the tablet's clock is off**, which every time-based rule needs?

The forces: a tablet may be offline for 72 h and then upload hundreds of commands over a weak 3G link. An operator
must see a final status for each entry on the tablet without a second round trip. A signed field fact is evidence: it
must never be lost or silently changed. And the shift-change burst (300 tablets at once) must stay under a second
per batch.

## Decision

### 1. Handled in the upload request, with a final answer per command

Commands are verified **and handled** while the upload request runs. The answer lists a final status per command:
`accepted`, `duplicate`, `conflict` or `rejected` (lower case, as P1.15 built them; plan amendment A2). The tablet
never polls to learn whether a command it sent was handled.

Work that depends on **other** facts that may arrive later (continuity across shifts, a late entry changing an
earlier one's flags) does not run in the request. Handlers raise outbox events
([ADR-0015](0015-transactional-outbox.md)), and the re-evaluation subscribes to them. That later result reaches the
tablet through the status pull (§6), not through the upload answer.

*Why not a background worker for everything:* the tablet would need a second channel to learn the status, the
operator would leave the site not knowing whether the entry is accepted, and the queue adds a failure mode (a stuck
worker) between the field and the office. Handlers are small (validate, store, flag); the expensive analytics are
the re-evaluations, and those are asynchronous anyway.

### 2. One transaction per batch, one savepoint per command, handlers join it

The upload request runs as **one database transaction**, in this order:

1. Take the **per-tablet advisory lock** (`pg_advisory_xact_lock`, key `sync.inbox:<device>`, already in place since
   finding C-5). Crossing uploads of one tablet now run one after the other. The second one sees the first one's
   rows and answers `duplicate` instead of 409. The 409 `sync.batch_conflict` stays in the contract for safety but
   becomes rare.
2. Read the device's last sequence and the already-stored command IDs (inside the lock, so they are current).
3. Decide each command: signature, revocation (quarantine, never handled: TC-P2-SYNC-010), sequence (§4), type (§5).
4. For each command to handle, in sequence order: **`SAVEPOINT`**, call the handler, write
   `sync.processed_commands (command_id, status, result, handled_at)`, and **`RELEASE`** the savepoint. If the
   handler throws, **`ROLLBACK TO SAVEPOINT`**: nothing of that command remains. The command is answered
   `rejected(sync.handler_failed, retryable: true)`, and the batch goes on (TC-P2-SYNC-009).
5. Insert the inbox rows, then commit.

**Handlers join the transaction.** The Sync module owns the connection and the transaction. It passes them to each
handler in an `ISyncTransaction` (connection, transaction, the command's savepoint name). The handler's module
context enlists in them through a building-block helper, `ModuleDbContext.JoinAsync(ISyncTransaction)`, which sets
the connection and calls `UseTransactionAsync`. This is the pattern the ledger already uses: `ILedgerWriter`
receives the caller's connection and transaction. So a handler's rows, its ledger entries (written by
`ModuleDbContext` in `CurrentTransaction`), its outbox events and Sync's bookkeeping commit together or not at all.
There is no distributed transaction: it is one PostgreSQL connection.

Rules that keep this safe (architecture tests):

- A handler never commits, never opens its own transaction and never calls a remote service. Outbox events are
  the only way out.
- A handler's work is bounded: no unbounded queries, and at most the rows of its own aggregate plus flags.
- The tenant comes from the device, as for every device request. The `SET LOCAL` of the unit of work covers every
  module's statements on the shared connection.

*Why a savepoint and not one transaction per command:* one command must not fail the batch (a handler bug in a new
command type would otherwise block every entry of the shift on that tablet), but the inbox, the sequence and the
answers must stay consistent. Savepoints give per-command isolation inside one consistent unit. They are cheap in
PostgreSQL at this size: at most 100 commands per batch, and 50 from the field app (amendment A9).

### 3. Business problems are never rejections

A field fact cannot be "wrong"; it can only be questioned. A handler answers one of three outcomes:

| Outcome | When | The tablet |
|---|---|---|
| `Accepted` | The fact is stored. | Marks it sent. |
| `Conflict(flags)` | The fact is stored **and flagged** for review: continuity mismatch, Δ too large, unknown asset on this site, end before start, a shift outside the pattern, clock skew. Final; never re-sent. | Marks it sent and shows "to be reviewed". |
| `Rejected(code, retryable)` | **Structure only**: signature invalid, payload does not match the type's schema, unknown type (§5), revoked device (quarantine), evidence blob missing or with a wrong hash (P2.2), a handler failure (retryable). | Keeps the command and shows the error. If retryable, it sends again later. |

The **tablet** blocks impossible entries before they are signed (plan amendment A3). The **server** may know more,
for example a later entry, so it stores and flags; it never refuses a signed fact, because a refused fact would stay
on the tablet forever and the evidence would be lost.

### 4. Gaps in the sequence: held, then handled in order

Today a command ahead of the next expected number is `rejected(sync.sequence_gap)` and the tablet keeps it. From
P2.1 it is **stored and held** (`waiting`). The answer lists `missingSequences`, and the tablet re-sends those
commands. When the gap closes, the held commands are handled in sequence order in the same request
(TC-P2-SYNC-003).

- A device may hold at most **500** waiting commands. Beyond that the answer is `rejected(sync.too_far_ahead)`,
  so a tablet cannot fill the inbox with high sequence numbers (T-12).
- A gap open longer than **24 h** raises a device alert (metric `karry_sync_waiting_commands`, alert and runbook "a
  tablet has a sequence gap"): data may be lost on that tablet, and someone must look at it.
- A **reused** number with a different command stays `rejected(sync.sequence_reused)`, as today.

### 5. Types are versioned; an unknown type is kept

Command types are versioned dotted names (`fieldops.shift_entry.start.v1`). Each handler declares the type it
handles. Registration goes through the module contract, like `ITenantProvisionedHandler`
([ADR-0018](0018-background-jobs-and-dispatcher.md)). Two handlers for one type stop the host at start-up.

An unknown type is answered `rejected(sync.type_unknown)`. The command is **kept in the inbox**, never dropped, and
the sequence does not advance (TC-P2-SYNC-013). A newer app talking to an older server loses nothing: after the
server is updated, the tablet's re-send is handled.

### 6. Status pull and clock offset

- **Status pull (US-P2-OFF-04 AC1):** `GET /sync/changes` gains a `submissions` section. It holds the current
  status of the commands this device sent and of the records its users created (accepted, conflict with flags,
  later "approved"/"rejected by the controller" from P2.8), as a delta by cursor, like the reference data.
- **Clock offset (US-P2-OFF-05):** every upload answer carries `serverTime`. The tablet measures its offset, and the
  server stores the device's last measured offset (`clock_offset_ms`, `clock_measured_at` on the device). Each
  handler receives `deviceTime`, `receivedAt` and the offset. The `CLOCK_SKEW` flag rule (P2.7) uses them.

### 7. Contracts (built in `Karry.BuildingBlocks.Sync`, §8)

```csharp
public interface ISyncCommandHandler
{
    string Type { get; }                                   // e.g. "fieldops.shift_entry.start.v1"
    Task<HandlerOutcome> HandleAsync(VerifiedCommand command, ISyncTransaction transaction, CancellationToken ct);
}

public sealed record VerifiedCommand(Guid CommandId, Guid DeviceId, Guid SiteId, long Sequence, string Type,
    JsonElement Payload, DateTimeOffset DeviceTime, DateTimeOffset ReceivedAt, int ClockOffsetMs, string SignedText);

public abstract record HandlerOutcome
{
    public sealed record Accepted(Guid? RecordId) : HandlerOutcome;
    public sealed record Conflict(Guid? RecordId, IReadOnlyList<string> Flags) : HandlerOutcome;
    public sealed record Rejected(string Code, bool Retryable) : HandlerOutcome;
}
```

A **contract test kit** in `Karry.Testing` runs the same checks against every handler: idempotency (the same
command twice gives one record and the handler is called once), replay, the rejection codes, and the savepoint
rollback when the handler throws. A handler for tests only (`test.echo.v1`, compiled only into test hosts) proves
the pipeline before the first real handler exists.

### 8. As built (P2.1, 2026-11-09)

- The contracts live in **`Karry.BuildingBlocks.Sync`** (`SyncCommands.cs`), next to `ISyncSource`, rather than in
  `Sync.Contracts`: modules already reference the building blocks for the pull side, and a handler then needs no
  reference to the Sync module. A module registers a handler with
  `services.AddSyncCommandHandler<THandler>("area.thing_done.v1")`; a second handler for a type throws while the host
  starts. `ModuleDbContext.JoinAsync(ISyncTransaction)` is the join helper of §2.
- `IInboxStore.OpenAsync` starts the transaction and takes the per-tablet lock **before** reading the stored IDs and
  the last sequence (§2 step 2; this also closes finding C-7, where the two reads ran apart).
- Each handler runs in its **own DI scope** with the device's tenant: a failed handler leaves nothing tracked behind
  for the next command. Savepoints are named `command_<sequence>`.
- Bookkeeping tables, append-only under RLS like the inbox: `sync.processed_commands` (outcome, record, flags, time,
  one row per received command) and `sync.unhandled_commands` (§5: the signed command and the reason, outside the
  sequence; a re-send while the type is still unknown adds nothing).
- The answer carries `retryable`, `flags` and `serverTime` (§6). The clock offset passed to handlers is 0 until slice 3
  stores the measured one. The status pull (§6) is slice 3.
- **Slice 2 (waiting commands, §4):** a number further on is kept in `sync.waiting_commands` (append-only; a row
  whose command reached the inbox has been handled) and answered `waiting`; the answer lists `missingSequences`
  (at most 100). When a command fills the gap, the waiting ones are handled in order **in the same request**, each in
  its savepoint; one that is not stored stops the run, and an earlier upload's waiting command then stays to be
  retried by the next upload. A command of the batch answered `waiting` and handled later in the same request is
  answered with its final status. Another command with a waiting command's number is `sync.sequence_reused`; beyond
  500 waiting, `sync.too_far_ahead` (retryable). `sync.sequence_gap` is no longer sent. Log event 7205 (waiting
  commands handled).
- **Slice 2b (the 24 h gap alert):** a Workers job every 5 minutes (`MeasureSyncGaps`, also
  `Karry.Workers measure-sync-gaps`) counts the still-waiting commands per tenant under RLS and reports two gauges
  (`karry_sync_waiting_commands` and `karry_sync_oldest_waiting_age_seconds`, nothing before the first measurement).
  A tablet past 24 h is logged by name (event 7206; 7207 when a company cannot be measured), so the metrics carry
  no device label. Alert `KarrySyncGapOpen` (ticket, 15 minutes), runbook
  [karry-sync-gap](../06-operations/runbooks/karry-sync-gap.md).
- **Slice 3 (status pull and clock, §6):** `GET /sync/changes` gains the collection **`submissions`**, from the
  Sync module's own `ISyncSource`. It has one row per command this tablet sent: `{id (the command ID), sequence, status, code,
  flags, recordId, at}`. The statuses are `accepted`/`conflict` from `processed_commands`, `waiting`, and `rejected` with
  `sync.type_unknown`; a handled command's outcome wins. A snapshot covers 14 days; a delta covers what changed since the
  cursor, under the usual 5-minute overlap. The field app ignores the collection until P2.2 stores it. The **clock offset** is
  measured by the server, not reported by the tablet: it is the difference between the time the tablet signed the upload
  (the assertion's `iat`, now on `DeviceCaller.IssuedAt`) and the server's receipt, so it is trustworthy and within the
  5-minute assertion window (whole seconds, plus network time). The last value is kept in `sync.device_clocks`, owned by
  Sync and updated rather than appended; it is not on the device row of Identity, which keeps the boundary. Handlers get it
  in `VerifiedCommand.ClockOffsetMs`. A waiting command keeps the **receipt time and offset of its arrival**
  (`waiting_commands.clock_offset_ms`), and its handler and inbox row use those, not the time the gap closed.
- Log events: 7203 (type unknown, warning), 7204 (handler failed, error, with the exception).

## Options considered

1. **Handle in the request, one savepoint per command (chosen).** A final answer per command, atomic bookkeeping,
   failures isolated, no new infrastructure.
2. **Store first, handle in a background worker.** The request is faster, but the tablet needs a second channel
   for statuses, a stuck worker silently stops the field, and the operator leaves without a final status.
   Rejected; kept only for the re-evaluations (§1).
3. **One transaction per command.** Failures are isolated, but the inbox and the sequence can disagree with the
   answers after a crash mid-batch, and there are 100 commits per batch. Rejected.
4. **Distributed transaction across module contexts** (`TransactionScope` with two connections). It escalates to
   prepared transactions in Npgsql, which is fragile and slow. Rejected: one connection, shared.
5. **Reject business problems** (the plan's first wording, `CONFLICT` beside `REJECTED` as an error). A rejected
   signed fact would stay on the tablet forever and the evidence would be lost. Rejected (amendments A2, A3).

## Consequences

- **Positive:** the operator sees a final status at once. A handler bug costs one command, not the shift.
  Bookkeeping, records, ledger and outbox commit together. Crossing uploads become duplicates instead of 409s.
  Newer apps never lose data on older servers.
- **Negative:** the upload request holds the tablet's lock and one connection for the whole batch, so handlers must
  stay small (§2 rules; load test: 50 commands, p95 < 1 s). Module contexts must support joining a transaction
  (`JoinAsync`), which is a new building block with its own tests. Held commands add a state to the inbox and a
  metric to watch.
- **Follow-ups:** P2.1 builds the pipeline, the contracts, the test kit, `test.echo.v1`, the status pull and the
  offset. P2.2 adds the blob check to the structural rejections (ADR-0032). P2.4 writes the first real handlers.
  The 24 h gap alert and its runbook ship with P2.1.

## Security review (to complete when P2.1 is done)

| Threat | Control | Test |
|---|---|---|
| T-02 forged device | Signature verified before any handler; revoked devices are quarantined and never handled | TC-P2-SYNC-010 |
| T-03 tampered payload | The handler receives only the verified signed text, parsed once | contract test kit |
| T-05 replay / reordering | Duplicates by `commandId`; strict sequence; held commands handled in order | TC-P2-SYNC-001, 003 |
| T-12 flooding | 100 × 64 KB per batch, rate limits per tablet, at most 500 held commands | `sync.too_far_ahead` test, k6 `sync-batches` |
