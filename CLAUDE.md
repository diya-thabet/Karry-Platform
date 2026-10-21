# Karry Platform — Instructions for Claude Code

Before doing anything in this repository, read these in order:

1. `docs/00-llm/llm_hands_off.md`: project memory: decisions, known mistakes, current state, next steps. **Update it before ending every session.**
2. `docs/00-llm/llm_guidelines.md`: mandatory engineering rules (testing, CI, security, git, docs, Definition of Done).
3. `docs/README.md`: map of all docs and a "where do I find…" lookup table. Open only the files your task needs:
   - business & pricing → `docs/01-product/`
   - architecture & stack → `docs/02-architecture/`
   - business logic, formulas, rules → `docs/03-domain/`
   - user stories, acceptance criteria, test cases → `docs/04-phases/phase-NN-*/plan.md`

Core rules (details in the guidelines):
- No code is done until it is tested, the tests pass locally, and GitHub Actions runs them.
- Never break the product invariants (tenant isolation, immutable signed records, ledger in the same transaction, explicit units, decimal money, offline never loses data).
- Never claim tests pass without running them; report failures honestly.
- Keep the docs organized: one topic per file, in its area folder, with front matter; run `python3 tools/docs/check_links.py` (must report 0 broken links).
