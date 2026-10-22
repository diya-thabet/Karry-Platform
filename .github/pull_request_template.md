<!-- Template: docs/00-llm/llm_guidelines.md §26.3. Small PRs (≤ 400 changed lines). No AI attribution lines. -->

## What & why
<!-- Summary and business reason. -->

## Stories / issues
<!-- US-Px-AREA-nn (AC1–ACn), closes #<issue> -->

## AC → tests mapping
| AC | Test(s) | Level |
|---|---|---|
| AC1 | `Test_name` (TC-Px-AREA-nnn) | Unit / Integration / E2E |

## How tested
- Local: <!-- commands → results -->
- CI: <!-- new tests visible in job X; test count before → after -->
- Mutation check (security/math code): <!-- injected bug → which test failed -->

## Security / tenancy notes
<!-- Threats considered (docs/02-architecture/threat-model.md), isolation/masking tests added, or "None". -->

## Migrations & rollout
<!-- Expand/contract step, backfill, feature flag, rollback plan, or "None". -->

## Screenshots / recordings
<!-- UI changes in FR and EN, or "None". -->

## Docs updated
<!-- ADR / formulas / help / test plan rows / tracker / llm_hands_off.md -->

## Checklist
- [ ] Every AC maps to at least one test; test names carry `TC-…` IDs
- [ ] Tests pass locally and run in CI (new tests visible in the run)
- [ ] No secrets, no real customer data, no AI attribution
- [ ] Sensitive area (Tenancy, Identity, Trust, Billing, Sync, ledger migrations, CI security)? → owner approval required before merge
