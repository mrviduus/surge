using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Configuration;
using LoadSurge.Models;
using LoadSurge.Runner;

namespace LoadSurge.Tests.Unit
{
    /// <summary>
    /// Tests for live progress reporting via IProgress&lt;LoadProgress&gt;.
    /// Uses a synchronous inline sink to avoid SynchronizationContext timing flakiness.
    /// </summary>
    public class ProgressReportingTests
    {
        /// <summary>IProgress sink that records reports synchronously on the reporting thread.</summary>
        private sealed class InlineProgress : IProgress<LoadProgress>
        {
            public ConcurrentQueue<LoadProgress> Reports { get; } = new();
            public void Report(LoadProgress value) => Reports.Enqueue(value);
        }

        [Fact]
        public async Task Reports_Snapshots_During_Run_And_Final_One()
        {
            // Arrange - 1.5s run, 200ms cadence => at least a few interim reports + the closing one.
            var progress = new InlineProgress();
            var plan = new LoadExecutionPlan
            {
                Name = "progress",
                Settings = new LoadSettings
                {
                    Concurrency = 5,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromMilliseconds(1500),
                    TerminationMode = TerminationMode.CompleteCurrentInterval
                },
                Action = async () =>
                {
                    await Task.Delay(10);
                    return true;
                }
            };
            var config = new LoadWorkerConfiguration
            {
                Progress = progress,
                ProgressInterval = TimeSpan.FromMilliseconds(200)
            };

            // Act
            var result = await LoadRunner.Run(plan, config, TestContext.Current.CancellationToken);

            // Assert - several reports arrived and the last one matches the final result.
            Assert.True(progress.Reports.Count >= 3,
                $"Expected several progress reports, got {progress.Reports.Count}");

            var last = default(LoadProgress);
            foreach (var report in progress.Reports)
                last = report;

            Assert.NotNull(last);
            Assert.Equal(result.Total, last!.Completed);
            Assert.Equal(result.RequestsStarted, last.RequestsStarted);
            Assert.Equal(0, last.InFlight);
        }

        [Fact]
        public async Task Progress_Counters_Are_Monotonic()
        {
            // Arrange
            var progress = new InlineProgress();
            var plan = new LoadExecutionPlan
            {
                Name = "monotonic",
                Settings = new LoadSettings
                {
                    Concurrency = 10,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(1),
                    TerminationMode = TerminationMode.CompleteCurrentInterval
                },
                Action = () => Task.FromResult(true)
            };
            var config = new LoadWorkerConfiguration
            {
                Progress = progress,
                ProgressInterval = TimeSpan.FromMilliseconds(100)
            };

            // Act
            await LoadRunner.Run(plan, config, TestContext.Current.CancellationToken);

            // Assert - started/completed/elapsed never go backwards between reports.
            LoadProgress? previous = null;
            foreach (var report in progress.Reports)
            {
                if (previous != null)
                {
                    Assert.True(report.RequestsStarted >= previous.RequestsStarted, "RequestsStarted regressed");
                    Assert.True(report.Completed >= previous.Completed, "Completed regressed");
                    Assert.True(report.ElapsedSeconds >= previous.ElapsedSeconds, "ElapsedSeconds regressed");
                }
                Assert.Equal(report.Completed, report.Success + report.Failure);
                previous = report;
            }
        }

        [Fact]
        public async Task Throwing_Progress_Consumer_Does_Not_Break_The_Run()
        {
            // Arrange
            var plan = new LoadExecutionPlan
            {
                Name = "throwing-progress",
                Settings = new LoadSettings
                {
                    Concurrency = 2,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromMilliseconds(500),
                    TerminationMode = TerminationMode.CompleteCurrentInterval
                },
                Action = () => Task.FromResult(true)
            };
            var config = new LoadWorkerConfiguration
            {
                Progress = new ThrowingProgress(),
                ProgressInterval = TimeSpan.FromMilliseconds(100)
            };

            // Act & Assert - run completes normally despite the misbehaving consumer.
            var result = await LoadRunner.Run(plan, config, TestContext.Current.CancellationToken);
            Assert.True(result.Total > 0);
        }

        private sealed class ThrowingProgress : IProgress<LoadProgress>
        {
            public void Report(LoadProgress value) => throw new InvalidOperationException("consumer bug");
        }

        [Fact]
        public async Task Zero_ProgressInterval_With_Progress_Set_Throws()
        {
            // Arrange
            var plan = new LoadExecutionPlan
            {
                Name = "bad-interval",
                Settings = new LoadSettings
                {
                    Concurrency = 1,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(1)
                },
                Action = () => Task.FromResult(true)
            };
            var config = new LoadWorkerConfiguration
            {
                Progress = new InlineProgress(),
                ProgressInterval = TimeSpan.Zero
            };

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => LoadRunner.Run(plan, config, TestContext.Current.CancellationToken));
        }
    }
}
