# Changelog

All notable changes to the LoadSurge project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.1.0] - 2026-07-11

### Added
- Live progress reporting: `LoadWorkerConfiguration.Progress` (`IProgress<LoadProgress>`) + `ProgressInterval` (default 1s). Snapshots carry elapsed, started/completed/success/failure, in-flight, dropped, and running RPS; a closing snapshot is always delivered before `Run` returns. A throwing progress consumer cannot break the run.

## [3.0.0] - 2026-07-11

### Changed
- **BREAKING (internals):** Removed Akka.NET entirely - LoadSurge now has zero external dependencies
- New open-workload-model engine (constant arrival rate, NBomber `Inject` / k6 `constant-arrival-rate` style): iterations are injected on schedule regardless of response times; in-flight requests accumulate under a slow system instead of being silently throttled by a worker pool
- Task-per-arrival execution replaces the fixed worker pool and channel
- Lock-striped metrics collector; hot path is allocation-free (`Stopwatch.GetTimestamp`, no per-request message objects)
- Public API unchanged: `LoadRunner.Run`, `LoadExecutionPlan`, `LoadSettings`, `LoadResult`, `TerminationMode`

### Added
- `LoadSettings.RequestTimeout` - per-request timeout; hung requests are counted as failures
- `LoadWorkerConfiguration.MaxInFlight` - safety cap on concurrent executions; excess iterations are dropped and counted (k6-style dropped iterations)
- `LoadResult.Dropped` - number of iterations dropped by the MaxInFlight cap
- `LoadRunner.Run(plan, configuration, cancellationToken)` - cancelling stops scheduling, cancels cancellation-aware actions, and returns partial results
- `LoadExecutionPlan.ActionWithCancellation` - cancellation-aware action; the token fires on `RequestTimeout` and run cancellation, so timed-out work is truly aborted instead of leaking
- Input validation with actionable messages: `Concurrency >= 1`, `Duration > 0`, `Interval > 0` (zero interval previously spun the scheduler at 100% CPU), positive `MaxIterations`/`RequestTimeout`/`MaxInFlight`, non-negative `GracefulStopTimeout`, exactly one action set
- Public API surface tracking via Microsoft.CodeAnalysis.PublicApiAnalyzers (`PublicAPI.Shipped.txt`) - build fails on accidental breaking changes
- Code analysis: `AnalysisLevel: latest-recommended`, `.editorconfig`
- `benchmarks/LoadSurge.Benchmarks` (BenchmarkDotNet) - verifies the zero-allocation hot path (`RequestStarted + RecordResult` ≈ 8-16 ns, 0 B)

### Deprecated
- `LoadWorkerConfiguration.Mode`, `MaxWorkerThreads`, `ChannelCapacity` - obsolete, ignored (single engine, no worker pool/channels)

### Fixed
- Race between test duration expiry and graceful shutdown that could skip the in-flight drain
- Workers being cancelled at duration expiry, making the grace period ineffective
- Failure latency (0 ms) skewing MinLatency and percentiles - failures are now excluded from latency statistics
- Partial batches under `MaxIterations` reported with wrong item count

## [1.0.1] - 2025-10-22

### Added
- CLAUDE.md file with comprehensive codebase guidance for Claude Code AI assistant

### Changed
- Updated all documentation references from "Surge" to "LoadSurge"
- Improved README.md with correct NuGet package links and GitHub repository URL
- Updated CHANGELOG.md and PROGRESS.md to reference LoadSurge consistently

### Fixed
- Corrected NuGet badge to point to LoadSurge package
- Fixed xUnit integration references in documentation

## [1.0.0] - 2025-10-21

### Added
- Initial release of LoadSurge as a standalone, framework-agnostic load testing engine
- Extracted core functionality from xUnitV3LoadFramework v2.0.0
- Actor-based architecture using Akka.NET 1.5.54
- LoadRunner for orchestrating load test execution
- LoadWorkerActorHybrid for high-performance channel-based execution (100k+ RPS)
- LoadWorkerActor for task-based execution (moderate load scenarios)
- ResultCollectorActor for comprehensive metrics aggregation
- Three termination modes: Duration, CompleteCurrentInterval, StrictDuration
- Configurable graceful shutdown with automatic timeout calculation
- Comprehensive performance metrics including:
  - Request counts (total, success, failed)
  - Throughput (requests per second)
  - Latency statistics (min, max, average, median, P95, P99)
  - Resource utilization (worker threads, memory usage)
- Support for .NET 8.0
- MIT License
- Comprehensive test suite with 5 core unit tests
- Full XML documentation for all public APIs

### Changed
- Namespace migration from `xUnitV3LoadFramework.LoadRunnerCore.*` to `LoadSurge.*`
- Updated all internal references to use new LoadSurge namespaces

### Technical Details
- Target Framework: .NET 8.0
- Language Version: C# 12
- Key Dependencies:
  - Akka.NET 1.5.54
  - Microsoft.SourceLink.GitHub 8.0.0 (build-time)
- Package Structure:
  - LoadSurge (core package)
  - LoadSurge.Tests (test project)

### Migration Notes
For users migrating from xUnitV3LoadFramework v2.x:
- The core load testing engine is now available as the `LoadSurge` package
- xUnit-specific features (LoadAttribute, LoadTestRunner) remain in the xUnitV3LoadFramework package
- Direct LoadRunner users: Update `using xUnitV3LoadFramework.LoadRunnerCore.*` to `using LoadSurge.*`
- See PROGRESS.md for detailed migration information

## Future Releases

### [Planned for 1.1.0]
- Additional performance optimizations
- Enhanced metrics and reporting capabilities
- Support for custom result collectors
- Performance profiling tools

### [Planned for 2.0.0]
- Support for distributed load testing across multiple nodes
- Built-in result persistence
- Real-time metrics streaming
- Dashboard integration

---

For integration with xUnit v3, see [xUnitV3LoadFramework](https://github.com/mrviduus/xUnitV3LoadFramework)
