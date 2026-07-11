# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LoadSurge is a high-performance, dependency-free load testing framework for .NET. It implements an **open workload model** (constant arrival rate, NBomber `Inject` / k6 `constant-arrival-rate` style) and can be integrated with any testing framework or used standalone.

**Key Technologies:**
- Library multi-targets `netstandard2.0;net8.0`: netstandard2.0 for reach (.NET Framework 4.7.2+, .NET 6+), net8.0 for Native-AOT/trimming support (`IsAotCompatible`, `IsTrimmable`). Built with the .NET 8 SDK (`global.json` pins `8.0.100`, `rollForward: latestFeature`).
- **Zero external dependencies** (Akka.NET was removed in v3.0.0)
- xUnit v3 (Testing; test project targets `net8.0`)
- NuGet package published as `LoadSurge` (current version 3.1.0)
- CA1510 (`ThrowIfNull`) is disabled in `.editorconfig`: not available on netstandard2.0, single code path kept for both TFMs

**Note:** `ImplicitUsings` is **disabled** — every file declares explicit `using` directives and uses full `namespace X { }` block style. Match this when adding files.

## Build & Development Commands

### Build
```bash
dotnet restore
dotnet build
dotnet build --configuration Release   # TreatWarningsAsErrors here
```

### Testing
```bash
# Run all tests
dotnet test

# Run a specific test class / method
dotnet test --filter "FullyQualifiedName~LoadEngineTests"
dotnet test --filter "FullyQualifiedName~LoadEngineTests.MaxIterations_Executes_Exactly_N_Times"

# Run tests excluding CI-flaky tests
dotnet test --filter "Category!=CI-Flaky"

# Code coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults
```

### Package
```bash
dotnet pack src/LoadSurge/LoadSurge.csproj --configuration Release
dotnet pack src/LoadSurge/LoadSurge.csproj -p:Version=3.0.1
```

### Benchmarks
```bash
# Full run (slow); proves the zero-alloc hot path via MemoryDiagnoser
dotnet run -c Release --project benchmarks/LoadSurge.Benchmarks

# Quick check of a single suite
dotnet run -c Release --project benchmarks/LoadSurge.Benchmarks -- --filter "*MetricsCollector*" --job short
```
Baseline (Apple Silicon, short job): `RequestStarted + RecordResult` ≈ 8-16 ns, **0 B allocated**. A PR that introduces allocations in the hot path must be rejected or justified.

## Architecture Overview

### Open Workload Model (v3.0.0+)

The scheduler injects `Concurrency` iterations every `Interval` at absolute-time slots, **regardless of whether previous responses have returned**. Under a slow system, in-flight requests accumulate (Little's Law: in-flight ≈ rate × latency) — this is deliberate and is what an open model must measure. There is no worker pool: a pool would silently throttle injection and convert the open model into a closed one.

```
LoadRunner.Run(plan)                     (Runner/LoadRunner.cs - thin entry point)
    ↓
LoadEngine.RunAsync                      (Engine/LoadEngine.cs)
    │  scheduler loop: batch every Interval at absolute-time slots (no drift)
    │  per iteration: task-per-arrival on the thread pool
    │  optional MaxInFlight cap → drop + count (k6-style dropped iterations)
    │  after Duration: graceful drain up to EffectiveGracefulStopTimeout
    ↓
MetricsCollector                         (Engine/MetricsCollector.cs)
    │  lock-striped accumulators (one stripe per core) - near-zero contention
    │  Interlocked in-flight counter; latencies merged + sorted once at the end
    ↓
LoadResult
```

**Hot-path rules (keep it allocation-free):**
- `Stopwatch.GetTimestamp()` for all timing — never `DateTime.UtcNow`, never `Stopwatch.StartNew()` per request
- No per-request object allocations; results go into lock-striped accumulators
- Failed requests are counted but their latency is **excluded** from latency statistics (would skew MinLatency/percentiles)

### Termination & Limits

**TerminationMode:**
- `Duration` - Stop scheduling when elapsed ≥ Duration, then drain in-flight up to grace period
- `CompleteCurrentInterval` - Schedule every batch whose slot begins within Duration (predictable counts), then drain
- `StrictDuration` - Stop scheduling at Duration, **no drain**; unfinished work reported in `RequestsInFlight`

The run spans the full `Duration` window (Time/RPS are schedule-normalized), except `MaxIterations` completes early once the budget is spent.

**LoadSettings:**
- `MaxIterations` (nullable) - Stop after exactly N executions; dropped iterations do not consume the budget
- `RequestTimeout` (nullable) - Per-request timeout; hung requests counted as failures. With `ActionWithCancellation` the token fires on timeout (work truly aborted); with legacy `Action` the task keeps running unobserved
- `GracefulStopTimeout` (nullable) - Default: 30% of Duration, clamped to [5s, 60s] (`EffectiveGracefulStopTimeout`)

**Actions:** exactly one of `LoadExecutionPlan.Action` (`Func<Task<bool>>`) or `ActionWithCancellation` (`Func<CancellationToken, Task<bool>>`, preferred) must be set. Both set or both null → validation error.

**Cancellation:** `LoadRunner.Run(plan, config, ct)` - cancelling stops scheduling, cancels in-flight cancellation-aware actions, skips drain, and **returns partial results** (does not throw `OperationCanceledException` - documented deliberate deviation, partial data is the point of a load test).

**Validation:** `LoadRunner.Validate` fails fast: `Concurrency >= 1`, `Duration > 0`, `Interval > 0` (zero would spin the scheduler), positive `MaxIterations`/`RequestTimeout`/`MaxInFlight`, non-negative `GracefulStopTimeout`.

**LoadWorkerConfiguration:**
- `MaxInFlight` (nullable, default null = unlimited) - Safety cap protecting the test process from OOM when the SUT hangs; excess iterations are dropped and counted in `LoadResult.Dropped`
- `Progress` (`IProgress<LoadProgress>?`) + `ProgressInterval` (default 1s) - live snapshots during the run plus a closing snapshot before `Run` returns; a throwing consumer never breaks the run (reporting loop in `LoadEngine.ReportProgressAsync`)
- `Mode`, `MaxWorkerThreads`, `ChannelCapacity` - **obsolete since v3.0.0, ignored**. Do not use in new code

### LoadResult Metrics

- Core: `Total` (= Success + Failure), `Success`, `Failure`, `Time` (s), `RequestsPerSecond`
- Latency (ms, successes only): `Min/Average/Median/Percentile95/Percentile99/MaxLatency`
- Flow: `RequestsStarted`, `RequestsInFlight` (unfinished at test end), `Dropped` (MaxInFlight cap), `BatchesCompleted`
- Queue: `AvgQueueTime`/`MaxQueueTime` (ms) - lag between an iteration's scheduled slot and actual execution start (thread-pool scheduling lag)
- `PeakMemoryUsage` (bytes) - `GC.GetTotalMemory(false)` sampled every 1024th request start

Percentiles use the ceiling method over the sorted latency array: `sorted[ceil(p/100 * n) - 1]`.

## Code Patterns & Conventions

### Test Actions Must Be:
1. **Thread-safe** - called concurrently
2. **Idempotent** - safe to retry
3. **Return Task<bool>** - true = success, false = failure

```csharp
Action = async () =>
{
    try
    {
        var response = await httpClient.GetAsync(url);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false; // Mark as failure
    }
}
```

### Internals & Testability
- `Engine/` classes are `internal`; tests access them via `InternalsVisibleTo("LoadSurge.Tests")` in the csproj
- The engine must never throw from a spawned iteration — `ExecuteOneAsync` catches everything and records a failure

## Testing Guidelines

### Test File Organization
All tests in `tests/LoadSurge.Tests/Unit/`:
- `LoadEngineTests.cs` - Open-model semantics: slow responses, MaxInFlight drops, RequestTimeout, StrictDuration, graceful drain
- `MetricsCollectorTests.cs` - Percentiles, failure-latency exclusion, concurrent-recording exactness
- `ValidationTests.cs` - Fail-fast input validation
- `CancellationTests.cs` - Run cancellation, token propagation, timeout-cancels-work
- `ProgressReportingTests.cs` - Live progress cadence, monotonic counters, misbehaving consumers
- `RequestCountAccuracyTests.cs` - Request counting per termination mode
- `GracefulStopConfigurationTests.cs` - Shutdown behavior
- `LoadRunnerTimeoutTests.cs` - End-to-end completion without hangs
- `HighConcurrencyTests.cs`, `BackwardCompatibilityTests.cs` - Load/compat coverage

### Timing Variance in Tests
- Allow ±10% (or looser) variance for request count assertions in timing-sensitive tests
- Use `Assert.True(result.Total >= X && result.Total <= Y)` pattern
- Only `MaxIterations` tests may assert exact counts

## CI/CD

### GitHub Actions Workflow (`.github/workflows/ci-cd.yml`)
- **Build Job:** Restore → Build → Test → Upload Coverage
- **Package Job:** Triggers on main branch or tags → Publishes to NuGet
- **Security Job:** Scans for vulnerabilities

### NuGet Publishing
- Automatic on main branch commits — CI reads the base version from the csproj and publishes `{base}.{GITHUB_RUN_NUMBER}` (e.g. `3.0.0.42`).
- Automatic on version tags (e.g., `v3.0.1` uses exact tag version)
- Requires `NUGET_API_KEY` secret in repository

## Common Scenarios

### Adding New Termination Mode
1. Add enum value to `Models/TerminationMode.cs`
2. Implement in the scheduler loop and drain logic in `Engine/LoadEngine.cs`
3. Add tests in `LoadEngineTests.cs` / `RequestCountAccuracyTests.cs`

### Adding New Metrics
1. Add accumulation to `Engine/MetricsCollector.cs` (stripe field for hot-path data, `Interlocked` for global counters)
2. Add property to `LoadResult`, populate in `BuildResult`
3. Add tests in `MetricsCollectorTests.cs`

## Project Structure

```
LoadSurge/
├── src/LoadSurge/
│   ├── Engine/              # LoadEngine (scheduler + execution), MetricsCollector
│   ├── Configuration/       # LoadWorkerConfiguration (MaxInFlight; obsolete legacy knobs)
│   ├── Models/              # LoadExecutionPlan, LoadResult, LoadSettings, TerminationMode
│   ├── Runner/              # LoadRunner entry point + validation
│   └── PublicAPI.*.txt      # Declared public API surface (analyzer-enforced)
├── tests/LoadSurge.Tests/Unit/
├── benchmarks/LoadSurge.Benchmarks/  # BenchmarkDotNet (zero-alloc proof)
├── samples/LoadSurge.Samples/        # Runnable offline examples
├── .github/workflows/       # CI/CD
├── .editorconfig            # Style conventions
├── Directory.Packages.props # Central package management
└── LoadSurge.sln
```

### Build Configuration
- `TreatWarningsAsErrors: true` (Release mode)
- `Nullable: enable`, `LangVersion: 12`, `GenerateDocumentationFile: true` (XML docs required on public members)
- `AnalysisLevel: latest-recommended` - CA rules enabled; keep the build warning-free
- `Deterministic: true`, SourceLink + symbols

### Public API Tracking (PublicApiAnalyzers)
The public API surface is declared in `src/LoadSurge/PublicAPI.Shipped.txt` (released) and `PublicAPI.Unshipped.txt` (pending). Adding/changing/removing a public member without updating these files fails the build (RS0016/RS0017) — this is the guard against accidental breaking changes.
- New public API → add the entry to `PublicAPI.Unshipped.txt` (or run `dotnet format analyzers src/LoadSurge/LoadSurge.csproj --diagnostics RS0016`)
- On release → move Unshipped entries into Shipped
- Note: analyzer pinned to 3.3.4 — newer versions need a newer Roslyn than the .NET 8 SDK ships (CS9057)

## Backward Compatibility

- Public API stable since 1.x: `LoadRunner.Run`, `LoadExecutionPlan`, `LoadSettings`, `LoadResult`, `TerminationMode`
- **v3.0.0:** Akka.NET removed; `Mode`/`MaxWorkerThreads`/`ChannelCapacity` obsolete and ignored; single open-model engine
- **v2.0.0:** `required` keyword removed from model properties (netstandard2.0 consumers). Do not reintroduce `required` on public models
- Tests in `BackwardCompatibilityTests.cs` ensure compatibility

## References

- **Repository:** https://github.com/mrviduus/LoadSurge
- **NuGet Package:** https://www.nuget.org/packages/LoadSurge
- **Parent Project:** https://github.com/mrviduus/xUnitV3LoadFramework
