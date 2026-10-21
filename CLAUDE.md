# Karry Platform — Instructions for Claude Code

Before doing anything in this repository, read these in order:

1. `docs/llm_hands_off.md`: project memory: decisions, known mistakes, current state, next steps. **Update it before ending every session.**
2. `docs/llm_guidelines.md`: mandatory engineering rules (testing, CI, security, git, Definition of Done).
3. `docs/MASTER_PLAN.md`: architecture, stack, corrected formulas.
4. `docs/PHASES_DETAILED_PLAN.md`: phases, user stories (`US-…`), acceptance criteria, test cases (`TC-…`).

Core rules (details in the guidelines):
- No code is done until it is tested, the tests pass locally, and GitHub Actions runs them.
- Never break the product invariants (tenant isolation, immutable signed records, ledger in the same transaction, explicit units, decimal money, offline never loses data).
- Never claim tests pass without running them; report failures honestly.
