using System;
using System.Threading;
using System.Threading.Tasks;
using LoadSurge.Configuration;
using LoadSurge.Models;
using LoadSurge.Runner;

namespace LoadSurge.Samples
{
    /// <summary>
    /// Runnable examples: dotnet run --project samples/LoadSurge.Samples
    /// Uses a simulated workload (no network needed) so the samples always work offline.
    /// </summary>
    public static class Program
    {
        public static async Task Main()
        {
            await BasicRunAsync();
            await LiveProgressWithTimeoutsAsync();
        }

        /// <summary>Minimal usage: constant arrival rate against a simulated 20-50ms workload.</summary>
        private static async Task BasicRunAsync()
        {
            Console.WriteLine("== Basic run: 100 RPS for 3 seconds ==");

            var plan = new LoadExecutionPlan
            {
                Name = "Basic_100RPS",
                Settings = new LoadSettings
                {
                    Concurrency = 10,                             // 10 iterations...
                    Interval = TimeSpan.FromMilliseconds(100),    // ...every 100ms = 100 RPS
                    Duration = TimeSpan.FromSeconds(3),
                    TerminationMode = TerminationMode.CompleteCurrentInterval
                },
                Action = static async () =>
                {
                    // Simulated I/O: replace with an HttpClient call, DB query, etc.
                    await Task.Delay(Random.Shared.Next(20, 50));
                    return Random.Shared.Next(100) < 98; // ~2% failures
                }
            };

            var result = await LoadRunner.Run(plan);

            Console.WriteLine($"Total: {result.Total}, Success: {result.Success}, Failed: {result.Failure}");
            Console.WriteLine($"RPS: {result.RequestsPerSecond:F1}, Avg: {result.AverageLatency:F1}ms, " +
                              $"P95: {result.Percentile95Latency:F1}ms, P99: {result.Percentile99Latency:F1}ms");
            Console.WriteLine();
        }

        /// <summary>
        /// Full feature tour: cancellation-aware action, per-request timeout,
        /// MaxInFlight safety cap, and live progress every second.
        /// </summary>
        private static async Task LiveProgressWithTimeoutsAsync()
        {
            Console.WriteLine("== Live progress: slow SUT, RequestTimeout=250ms, MaxInFlight=200 ==");

            var plan = new LoadExecutionPlan
            {
                Name = "Progress_Demo",
                Settings = new LoadSettings
                {
                    Concurrency = 50,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(5),
                    TerminationMode = TerminationMode.CompleteCurrentInterval,
                    RequestTimeout = TimeSpan.FromMilliseconds(250)
                },
                // Preferred action shape: honors the token, so timeouts truly abort the work.
                ActionWithCancellation = static async token =>
                {
                    // 10% of requests "hang" and get cut off by RequestTimeout.
                    var delay = Random.Shared.Next(100) < 10 ? 5_000 : Random.Shared.Next(30, 120);
                    await Task.Delay(delay, token);
                    return true;
                }
            };

            var config = new LoadWorkerConfiguration
            {
                MaxInFlight = 200, // safety valve: excess iterations are dropped + counted
                Progress = new Progress<LoadProgress>(static p =>
                    Console.WriteLine($"[{p.ElapsedSeconds,4:F1}s] started={p.RequestsStarted,5} " +
                                      $"ok={p.Success,5} fail={p.Failure,4} inflight={p.InFlight,4} " +
                                      $"dropped={p.Dropped,4} rps={p.RequestsPerSecond,6:F0}"))
            };

            var result = await LoadRunner.Run(plan, config, CancellationToken.None);

            Console.WriteLine($"Done. Total: {result.Total}, timeouts(fail): {result.Failure}, " +
                              $"dropped: {result.Dropped}, P99: {result.Percentile99Latency:F1}ms");
        }
    }
}
