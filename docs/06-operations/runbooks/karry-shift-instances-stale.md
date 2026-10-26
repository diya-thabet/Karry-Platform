---
title: "Runbook: KarryShiftInstancesStale"
area: operations
tags: [runbook, alert, organization, shifts, workers]
source: "P1.11, US-P1-ORG-02 AC3, ADR-0024"
status: baseline
last_updated: 2026-10-26
---

# KarryShiftInstancesStale

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** no daily shift generation in which every tenant succeeded for **26 hours**, or the worker reports no such time at all for 30 minutes (`karry_organization_shifts_last_success_seconds`; severity `ticket`).
- **Impact:** the stored shift instances (`organization.shift_instances`, the next 30 days of each site) stop moving forward. Nothing breaks at once: changes made in the portal still update the stored instances in the same transaction, and the computed range (`GET /sites/{id}/shift-instances`) always works. After about 30 days without a run, field devices have no shifts to attach entries to.
- **Diagnosis:**
  1. Is `karry-workers` running? It generates the instances daily at 00:15 UTC and once at start-up.
  2. Worker logs, event **7001** (`Shift instances of tenant {Tenant} could not be generated`): one tenant failing keeps the others going but makes the run incomplete. Usual causes: database unreachable, a stored pattern that no longer validates (the exception names the error code).
  3. Run it by hand: `Karry.Workers generate-shifts` prints tenants, sites and the instances added, changed and removed (exit 1 if a tenant failed).
- **Mitigation:** fix the cause and run `Karry.Workers generate-shifts`; the generator is idempotent, so it is safe to repeat. It never changes an instance that has started.
- **Resolution:** a complete run moves the last-success time forward and the alert clears.
- **Escalation:** ticket for the platform engineer within the working day.
