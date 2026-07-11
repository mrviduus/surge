# Contributing to LoadSurge

## Quick Start

```bash
dotnet restore
dotnet build
dotnet test
```

## Ground Rules

- **Zero dependencies** in `src/LoadSurge` — this is a core design constraint. PRs adding packages to the library will be declined.
- **Zero-allocation hot path** — `MetricsCollector.RequestStarted`/`RecordResult` must stay at 0 B. Verify with:
  ```bash
  dotnet run -c Release --project benchmarks/LoadSurge.Benchmarks -- --filter "*MetricsCollector*" --job short
  ```
- **Public API is tracked** — new/changed public members must be declared in `src/LoadSurge/PublicAPI.Unshipped.txt` or the build fails (RS0016). Breaking changes require a major version discussion first.
- **Release build must be warning-free** — `dotnet build -c Release` runs with `TreatWarningsAsErrors` and `AnalysisLevel: latest-recommended`.
- **Style** — explicit `using` directives (no implicit usings), block-scoped namespaces, XML docs on all public members. See `.editorconfig`.

## Tests

- Every behavior change needs a test in `tests/LoadSurge.Tests/Unit/`.
- Timing-sensitive assertions must tolerate CI variance (±10% or looser); only `MaxIterations` tests may assert exact counts.
- Full suite must pass: `dotnet test`.

## Commit / PR

- Follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `feat!:` for breaking).
- Update `CHANGELOG.md` under the appropriate version heading.
- One logical change per PR.
