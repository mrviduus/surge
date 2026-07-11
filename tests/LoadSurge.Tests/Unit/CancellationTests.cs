using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Models;
using LoadSurge.Runner;

namespace LoadSurge.Tests.Unit
{
    /// <summary>
    /// Tests for run-level cancellation and the cancellation-aware action path.
    /// </summary>
    public class CancellationTests
    {
        [Fact]
        public async Task Cancelling_Run_Returns_Partial_Results_Quickly()
        {
            // Arrange - a 30s test cancelled after ~500ms.
            var plan = new LoadExecutionPlan
            {
                Name = "cancel-run",
                Settings = new LoadSettings
                {
                    Concurrency = 5,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(30)
                },
                Action = async () =>
                {
                    await Task.Delay(10);
                    return true;
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var wallClock = Stopwatch.StartNew();

            // Act
            var result = await LoadRunner.Run(plan, null, cts.Token);
            wallClock.Stop();

            // Assert - returned promptly with the data collected so far, no exception.
            Assert.True(wallClock.Elapsed < TimeSpan.FromSeconds(5),
                $"Cancelled run must return quickly, took {wallClock.Elapsed}");
            Assert.True(result.RequestsStarted > 0, "Partial results must be preserved");
        }

        [Fact]
        public async Task Cancellation_Propagates_To_Cancellable_Actions()
        {
            // Arrange - actions block on the token; run cancellation must unwind them.
            var observedCancellations = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "cancel-actions",
                Settings = new LoadSettings
                {
                    Concurrency = 5,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(30)
                },
                ActionWithCancellation = async token =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), token);
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref observedCancellations);
                        throw;
                    }
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            // Act
            var result = await LoadRunner.Run(plan, null, cts.Token);

            // Give the cancelled actions a moment to unwind and record failures.
            await Task.Delay(500, TestContext.Current.CancellationToken);

            // Assert - actions saw the token; no 60s tasks leaked past cancellation.
            Assert.True(observedCancellations > 0,
                "Cancellable actions must observe run cancellation");
        }

        [Fact]
        public async Task RequestTimeout_Cancels_Cancellable_Action_Without_Leaking_Work()
        {
            // Arrange - the action honors its token; timeout must abort the work itself
            // (unlike the legacy Action path, which can only abandon it).
            var observedCancellations = 0;
            var plan = new LoadExecutionPlan
            {
                Name = "timeout-cancellable",
                Settings = new LoadSettings
                {
                    Concurrency = 3,
                    Interval = TimeSpan.FromMilliseconds(200),
                    Duration = TimeSpan.FromSeconds(1),
                    RequestTimeout = TimeSpan.FromMilliseconds(100),
                    GracefulStopTimeout = TimeSpan.FromSeconds(5)
                },
                ActionWithCancellation = async token =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), token);
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref observedCancellations);
                        throw;
                    }
                }
            };
            var wallClock = Stopwatch.StartNew();

            // Act
            var result = await LoadRunner.Run(plan);
            wallClock.Stop();

            // Assert - every request timed out as a failure and the work was truly cancelled.
            Assert.True(result.RequestsStarted > 0);
            Assert.Equal(0, result.Success);
            Assert.Equal(result.RequestsStarted, result.Failure);
            Assert.Equal(result.RequestsStarted, observedCancellations);
            Assert.Equal(0, result.RequestsInFlight);
            Assert.True(wallClock.Elapsed < TimeSpan.FromSeconds(6),
                $"Timed-out work must not delay the run, took {wallClock.Elapsed}");
        }

        [Fact]
        public async Task Already_Cancelled_Token_Returns_Empty_Result()
        {
            // Arrange
            var plan = new LoadExecutionPlan
            {
                Name = "pre-cancelled",
                Settings = new LoadSettings
                {
                    Concurrency = 5,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(10)
                },
                Action = () => Task.FromResult(true)
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var wallClock = Stopwatch.StartNew();

            // Act
            var result = await LoadRunner.Run(plan, null, cts.Token);
            wallClock.Stop();

            // Assert
            Assert.Equal(0, result.RequestsStarted);
            Assert.True(wallClock.Elapsed < TimeSpan.FromSeconds(2));
        }
    }
}
