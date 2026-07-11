using System;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Engine;

namespace LoadSurge.Tests.Unit
{
    /// <summary>
    /// Unit tests for the lock-striped MetricsCollector (Akka-free engine core).
    /// </summary>
    public class MetricsCollectorTests
    {
        [Fact]
        public void Percentiles_Use_Ceiling_Method()
        {
            // Arrange - latencies 1..100 ms
            var collector = new MetricsCollector("percentiles");
            collector.MarkStarted();
            for (var i = 1; i <= 100; i++)
            {
                collector.RequestStarted();
                collector.RecordResult(true, i);
            }

            // Act
            var result = collector.BuildResult();

            // Assert - ceiling method: index = ceil(p/100 * n) - 1
            Assert.Equal(50, result.MedianLatency);
            Assert.Equal(95, result.Percentile95Latency);
            Assert.Equal(99, result.Percentile99Latency);
            Assert.Equal(1, result.MinLatency);
            Assert.Equal(100, result.MaxLatency);
            Assert.Equal(50.5, result.AverageLatency, precision: 5);
        }

        [Fact]
        public void Failure_Latency_Is_Excluded_From_Latency_Statistics()
        {
            // Arrange
            var collector = new MetricsCollector("failures");
            collector.MarkStarted();

            collector.RequestStarted();
            collector.RecordResult(true, 10);
            collector.RequestStarted();
            collector.RecordResult(true, 20);
            collector.RequestStarted();
            collector.RecordResult(true, 30);
            collector.RequestStarted();
            collector.RecordResult(false, 0); // failure must not pollute latency stats

            // Act
            var result = collector.BuildResult();

            // Assert
            Assert.Equal(4, result.Total);
            Assert.Equal(3, result.Success);
            Assert.Equal(1, result.Failure);
            Assert.Equal(10, result.MinLatency); // not 0 from the failure
            Assert.Equal(30, result.MaxLatency);
            Assert.Equal(20, result.AverageLatency);
        }

        [Fact]
        public void Tracks_Started_InFlight_And_Dropped()
        {
            // Arrange - 5 started, 3 completed, 2 dropped
            var collector = new MetricsCollector("tracking");
            collector.MarkStarted();

            for (var i = 0; i < 5; i++)
                collector.RequestStarted();
            for (var i = 0; i < 3; i++)
                collector.RecordResult(true, 1);
            collector.RecordDropped();
            collector.RecordDropped();

            // Act
            var result = collector.BuildResult();

            // Assert
            Assert.Equal(5, result.RequestsStarted);
            Assert.Equal(2, result.RequestsInFlight); // 5 started - 3 completed
            Assert.Equal(2, result.Dropped);
            Assert.Equal(3, result.Total);
        }

        [Fact]
        public void Aggregates_Queue_Times()
        {
            // Arrange
            var collector = new MetricsCollector("queue");
            collector.MarkStarted();

            collector.RequestStarted();
            collector.RecordResult(true, 1, queueTimeMs: 10);
            collector.RequestStarted();
            collector.RecordResult(true, 1, queueTimeMs: 30);
            collector.RequestStarted();
            collector.RecordResult(true, 1); // no queue time - excluded from average

            // Act
            var result = collector.BuildResult();

            // Assert
            Assert.Equal(20, result.AvgQueueTime);
            Assert.Equal(30, result.MaxQueueTime);
        }

        [Fact]
        public void Empty_Run_Returns_Zeroed_Result_Without_Exceptions()
        {
            // Arrange
            var collector = new MetricsCollector("empty");
            collector.MarkStarted();

            // Act
            var result = collector.BuildResult();

            // Assert
            Assert.Equal("empty", result.ScenarioName);
            Assert.Equal(0, result.Total);
            Assert.Equal(0, result.MinLatency);
            Assert.Equal(0, result.MedianLatency);
            Assert.Equal(0, result.Percentile99Latency);
            Assert.Equal(0, result.RequestsPerSecond);
        }

        [Fact]
        public void Counts_Batches()
        {
            // Arrange
            var collector = new MetricsCollector("batches");
            for (var i = 0; i < 7; i++)
                collector.BatchCompleted();

            // Act & Assert
            Assert.Equal(7, collector.BuildResult().BatchesCompleted);
        }

        [Fact]
        public async Task Concurrent_Recording_Is_Exact()
        {
            // Arrange - hammer the collector from many tasks; counters must be exact
            var collector = new MetricsCollector("concurrent");
            collector.MarkStarted();
            const int tasks = 16;
            const int perTask = 10_000;

            // Act
            var work = new Task[tasks];
            for (var t = 0; t < tasks; t++)
            {
                var taskIndex = t;
                work[t] = Task.Run(() =>
                {
                    for (var i = 0; i < perTask; i++)
                    {
                        collector.RequestStarted();
                        // Half successes with latency 5ms, half failures
                        var success = (i & 1) == 0;
                        collector.RecordResult(success, success ? 5 : 0, queueTimeMs: taskIndex);
                    }
                }, TestContext.Current.CancellationToken);
            }
            await Task.WhenAll(work);

            var result = collector.BuildResult();

            // Assert - exact counts, no lost updates
            Assert.Equal(tasks * perTask, result.Total);
            Assert.Equal(tasks * perTask, result.RequestsStarted);
            Assert.Equal(tasks * perTask / 2, result.Success);
            Assert.Equal(tasks * perTask / 2, result.Failure);
            Assert.Equal(0, result.RequestsInFlight);
            Assert.Equal(5, result.MedianLatency);
            Assert.Equal(5, result.Percentile99Latency);
        }

        [Fact]
        public void Single_Sample_Percentiles_Are_That_Sample()
        {
            // Arrange
            var collector = new MetricsCollector("single");
            collector.MarkStarted();
            collector.RequestStarted();
            collector.RecordResult(true, 42);

            // Act
            var result = collector.BuildResult();

            // Assert
            Assert.Equal(42, result.MedianLatency);
            Assert.Equal(42, result.Percentile95Latency);
            Assert.Equal(42, result.Percentile99Latency);
            Assert.Equal(42, result.MinLatency);
            Assert.Equal(42, result.MaxLatency);
        }

        [Fact]
        public void Reports_Elapsed_Time_And_Rps()
        {
            // Arrange
            var collector = new MetricsCollector("rps");
            collector.MarkStarted();
            for (var i = 0; i < 100; i++)
            {
                collector.RequestStarted();
                collector.RecordResult(true, 1);
            }

            // Act
            var result = collector.BuildResult();

            // Assert - elapsed is tiny but positive; RPS consistent with Total/Time
            Assert.True(result.Time > 0);
            Assert.True(result.RequestsPerSecond > 0);
            Assert.Equal(result.Total / result.Time, result.RequestsPerSecond, precision: 5);
        }
    }
}
