using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Configuration;
using LoadSurge.Engine;
using LoadSurge.Models;

namespace LoadSurge.Tests.Unit
{
    /// <summary>
    /// Tests for the open-workload-model LoadEngine, focused on the scenario where
    /// the injection rate exceeds what the system under test can answer per second.
    /// Assertions use generous tolerances for CI timing variance.
    /// </summary>
    public class LoadEngineTests
    {
        private static LoadExecutionPlan CreatePlan(
            string name,
            LoadSettings settings,
            Func<Task<bool>> action)
        {
            return new LoadExecutionPlan
            {
                Name = name,
                Settings = settings,
                Action = action
            };
        }

        [Fact]
        public async Task Open_Model_Keeps_Injecting_When_Responses_Are_Slow()
        {
            // Arrange - 10 req / 100ms = 100 RPS, but each response takes 500ms.
            // A closed model would stall at ~concurrency; an open model must keep injecting.
            var settings = new LoadSettings
            {
                Concurrency = 10,
                Interval = TimeSpan.FromMilliseconds(100),
                Duration = TimeSpan.FromSeconds(1),
                TerminationMode = TerminationMode.CompleteCurrentInterval
            };
            var plan = CreatePlan("open-model", settings, async () =>
            {
                await Task.Delay(500);
                return true;
            });

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);

            // Assert - ~10 batches x 10 items = ~100 injected even though only ~2 response
            // "generations" fit into the duration. In-flight accumulation is the point.
            Assert.True(result.RequestsStarted >= 60,
                $"Open model must keep injecting; started only {result.RequestsStarted}");
            Assert.Equal(0, result.Dropped);
            // Graceful drain (min 5s) lets every started request finish.
            Assert.Equal(0, result.RequestsInFlight);
            Assert.Equal(result.RequestsStarted, result.Total);
            Assert.True(result.Success == result.Total, "All slow requests must succeed");
        }

        [Fact]
        public async Task MaxInFlight_Cap_Drops_Excess_Iterations_K6_Style()
        {
            // Arrange - responses take 2s, so in-flight would grow to ~100 without a cap.
            var settings = new LoadSettings
            {
                Concurrency = 10,
                Interval = TimeSpan.FromMilliseconds(100),
                Duration = TimeSpan.FromSeconds(1),
                TerminationMode = TerminationMode.StrictDuration
            };
            var plan = CreatePlan("max-in-flight", settings, async () =>
            {
                await Task.Delay(2000);
                return true;
            });
            var config = new LoadWorkerConfiguration { MaxInFlight = 5 };

            // Act
            var result = await LoadEngine.RunAsync(plan, config, TestContext.Current.CancellationToken);

            // Assert - cap honored, excess counted as dropped, nothing silently lost.
            Assert.True(result.Dropped > 0, "Expected dropped iterations under a tight cap");
            Assert.True(result.RequestsStarted <= 15,
                $"Cap of 5 should keep started low, got {result.RequestsStarted}");
            Assert.True(result.RequestsStarted + result.Dropped >= 60,
                "Started + dropped must account for the injection schedule");
        }

        [Fact]
        public async Task RequestTimeout_Converts_Hung_Requests_To_Failures()
        {
            // Arrange - action hangs for 10s, timeout is 200ms.
            var settings = new LoadSettings
            {
                Concurrency = 5,
                Interval = TimeSpan.FromMilliseconds(200),
                Duration = TimeSpan.FromSeconds(1),
                TerminationMode = TerminationMode.Duration,
                RequestTimeout = TimeSpan.FromMilliseconds(200),
                GracefulStopTimeout = TimeSpan.FromSeconds(5)
            };
            var plan = CreatePlan("request-timeout", settings, async () =>
            {
                await Task.Delay(10_000);
                return true;
            });

            var wallClock = Stopwatch.StartNew();

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);
            wallClock.Stop();

            // Assert - every request failed by timeout; the run never waits for the 10s hang.
            Assert.True(result.RequestsStarted > 0);
            Assert.Equal(0, result.Success);
            Assert.Equal(result.RequestsStarted, result.Failure);
            Assert.Equal(0, result.RequestsInFlight);
            Assert.True(wallClock.Elapsed < TimeSpan.FromSeconds(6),
                $"Run must not wait for hung requests, took {wallClock.Elapsed}");
        }

        [Fact]
        public async Task MaxIterations_Executes_Exactly_N_Times()
        {
            // Arrange - budget of 25 iterations, plenty of duration left.
            var settings = new LoadSettings
            {
                Concurrency = 10,
                Interval = TimeSpan.FromMilliseconds(50),
                Duration = TimeSpan.FromSeconds(5),
                MaxIterations = 25,
                TerminationMode = TerminationMode.CompleteCurrentInterval
            };
            var counter = 0;
            var plan = CreatePlan("max-iterations", settings, () =>
            {
                Interlocked.Increment(ref counter);
                return Task.FromResult(true);
            });

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);

            // Assert - exact: 2 full batches of 10 + partial batch of 5.
            Assert.Equal(25, counter);
            Assert.Equal(25, result.Total);
            Assert.Equal(25, result.Success);
            Assert.Equal(3, result.BatchesCompleted);
        }

        [Fact]
        public async Task StrictDuration_Returns_Immediately_And_Reports_InFlight()
        {
            // Arrange - 3s responses, 500ms test, strict cutoff.
            var settings = new LoadSettings
            {
                Concurrency = 5,
                Interval = TimeSpan.FromMilliseconds(100),
                Duration = TimeSpan.FromMilliseconds(500),
                TerminationMode = TerminationMode.StrictDuration
            };
            var plan = CreatePlan("strict-duration", settings, async () =>
            {
                await Task.Delay(3000);
                return true;
            });

            var wallClock = Stopwatch.StartNew();

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);
            wallClock.Stop();

            // Assert - no grace wait; unfinished work is visible, not hidden.
            Assert.True(wallClock.Elapsed < TimeSpan.FromSeconds(2),
                $"StrictDuration must not wait for in-flight, took {wallClock.Elapsed}");
            Assert.True(result.RequestsInFlight > 0, "In-flight requests must be reported");
            Assert.Equal(result.RequestsStarted, result.Total + result.RequestsInFlight);
        }

        [Fact]
        public async Task Graceful_Drain_Lets_InFlight_Requests_Complete()
        {
            // Arrange - last batch starts near the end of the 1s window; responses take 800ms.
            var settings = new LoadSettings
            {
                Concurrency = 4,
                Interval = TimeSpan.FromMilliseconds(250),
                Duration = TimeSpan.FromSeconds(1),
                TerminationMode = TerminationMode.CompleteCurrentInterval,
                GracefulStopTimeout = TimeSpan.FromSeconds(5)
            };
            var plan = CreatePlan("graceful-drain", settings, async () =>
            {
                await Task.Delay(800);
                return true;
            });

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);

            // Assert - every started request completed within the grace period.
            Assert.Equal(0, result.RequestsInFlight);
            Assert.Equal(result.RequestsStarted, result.Total);
            Assert.True(result.Total >= 12, $"Expected ~16 requests, got {result.Total}");
        }

        [Fact]
        public async Task Throwing_Action_Is_Counted_As_Failure_Not_Crash()
        {
            // Arrange
            var settings = new LoadSettings
            {
                Concurrency = 5,
                Interval = TimeSpan.FromMilliseconds(100),
                Duration = TimeSpan.FromMilliseconds(500),
                TerminationMode = TerminationMode.CompleteCurrentInterval
            };
            var plan = CreatePlan("throwing-action", settings,
                () => throw new InvalidOperationException("boom"));

            // Act
            var result = await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(result.Failure > 0);
            Assert.Equal(0, result.Success);
            Assert.Equal(result.Failure, result.Total);
            Assert.Equal(0, result.RequestsInFlight);
        }
    }
}
