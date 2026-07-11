using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using LoadSurge.Configuration;
using LoadSurge.Engine;
using LoadSurge.Models;

namespace LoadSurge.Benchmarks
{
    /// <summary>
    /// End-to-end engine throughput: a short high-rate run with a no-op action.
    /// Watches total allocations per completed iteration and wall-clock overhead.
    /// </summary>
    [MemoryDiagnoser]
    public class EngineBenchmarks
    {
        [Params(1_000, 10_000)]
        public int Concurrency { get; set; }

        [Benchmark]
        public async Task<LoadResult> OneSecond_Burst()
        {
            var plan = new LoadExecutionPlan
            {
                Name = "bench",
                Settings = new LoadSettings
                {
                    Concurrency = Concurrency,
                    Interval = TimeSpan.FromMilliseconds(100),
                    Duration = TimeSpan.FromSeconds(1),
                    TerminationMode = TerminationMode.CompleteCurrentInterval
                },
                Action = static () => Task.FromResult(true)
            };

            return await LoadEngine.RunAsync(plan, new LoadWorkerConfiguration(), CancellationToken.None);
        }
    }
}
