using BenchmarkDotNet.Attributes;
using LoadSurge.Engine;

namespace LoadSurge.Benchmarks
{
    /// <summary>
    /// Proves the hot-path claims: recording a result must be allocation-free
    /// and cheap enough for 100k+ RPS. Run: dotnet run -c Release --project benchmarks/LoadSurge.Benchmarks
    /// </summary>
    [MemoryDiagnoser]
    public class MetricsCollectorBenchmarks
    {
        private MetricsCollector _collector = null!;

        [GlobalSetup]
        public void Setup()
        {
            _collector = new MetricsCollector("benchmark");
            _collector.MarkStarted();
            // Pre-size stripe lists so List growth does not distort steady-state numbers.
            for (var i = 0; i < 100_000; i++)
            {
                _collector.RequestStarted();
                _collector.RecordResult(true, 5, 1);
            }
        }

        /// <summary>The full per-request accounting pair. Expected: 0 B allocated (amortized).</summary>
        [Benchmark(Baseline = true)]
        public void RequestStarted_Plus_RecordResult()
        {
            _collector.RequestStarted();
            _collector.RecordResult(true, 5.0, 1.0);
        }

        /// <summary>Failure path - no latency stored. Expected: 0 B allocated.</summary>
        [Benchmark]
        public void RecordResult_Failure()
        {
            _collector.RequestStarted();
            _collector.RecordResult(false, 0, 1.0);
        }

        /// <summary>MaxInFlight check read by the scheduler per iteration. Expected: 0 B.</summary>
        [Benchmark]
        public int InFlight_Read() => _collector.InFlight;
    }
}
