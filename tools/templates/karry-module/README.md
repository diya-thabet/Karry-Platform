# `karry-module` template

Creates a bounded-context module per [ADR-0001](../../../docs/adr/0001-modular-monolith.md) and the [repository structure](../../../docs/02-architecture/repository-structure.md). Use the wrapper script, which also adds the projects to `Karry.slnx`:

```bash
tools/new-module.sh Maintenance
```

Generated under `src/backend/Modules/<Name>/`:

| Project | References | Purpose |
|---|---|---|
| `Karry.Modules.<Name>.Contracts` | SharedKernel | The only project other modules may reference |
| `Karry.Modules.<Name>.Domain` | SharedKernel | Aggregates, value objects, rules; no EF/ASP.NET |
| `Karry.Modules.<Name>.Application` | Domain, Contracts, BuildingBlocks | Commands, queries, ports |
| `Karry.Modules.<Name>.Infrastructure` | Application | EF Core, adapters |
| `Karry.Modules.<Name>.Api` | Application, Infrastructure | Endpoints + `IModule` entry point |
| `Karry.Modules.<Name>.Tests` | Api | Module tests |

The rules are enforced by `tests/Karry.ArchitectureTests`. The template itself is tested by `tools/templates/test-template.sh` (TC-P1-PLT-003) in CI.
