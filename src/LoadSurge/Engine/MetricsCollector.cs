using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LoadSurge.Models;

namespace LoadSurge.Engine
{
    /// <summary>
    /// Lock-striped metrics accumulator for load test execution.
    /// Hot-path recording uses per-stripe locks (one stripe per CPU core) so concurrent
    /// tasks rarely contend. Aggregation into a LoadResult happens once, after the run.
    /// Failed operations are counted but their latency is excluded from latency statistics.
    /// </summary>
    internal sealed class MetricsCollector
    {
        private sealed class Stripe
        {
            public readonly object Lock = new object();
            public readonly List<double> Latencies = new List<double>();
            public int Success;
            public int Failure;
            public double QueueTimeSum;
            public double QueueTimeMax;
            public int QueueTimeCount;
        }

        private readonly Stripe[] _stripes;
        private readonly int _stripeMask;
        private readonly string _scenarioName;

        private int _started;
        private int _inFlight;
        private int _dropped;
        private int _batchesCompleted;
        private long _peakMemory;
        private long _startTimestamp;

        public MetricsCollector(string scenarioName)
        {
            _scenarioName = scenarioName;
            var stripeCount = RoundUpToPowerOfTwo(Environment.ProcessorCount);
            _stripeMask = stripeCount - 1;
            _stripes = new Stripe[stripeCount];
            for (var i = 0; i < stripeCount; i++)
                _stripes[i] = new Stripe();
        }

        /// <summary>Marks the start of the run; all elapsed-time metrics are relative to this point.</summary>
        public void MarkStarted()
        {
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>Records that a request began executing. Samples memory every 1024th request.</summary>
        public void RequestStarted()
        {
            var started = Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _inFlight);
            if ((started & 1023) == 1)
                SampleMemory();
        }

        /// <summary>
        /// Records a completed request. Latency of failed requests is excluded from
        /// latency statistics to avoid skewing MinLatency and percentiles.
        /// </summary>
        /// <param name="isSuccess">Whether the request succeeded.</param>
        /// <param name="latencyMs">Execution latency in milliseconds.</param>
        /// <param name="queueTimeMs">Delay between scheduling and execution start, in milliseconds.</param>
        public void RecordResult(bool isSuccess, double latencyMs, double queueTimeMs = 0)
        {
            Interlocked.Decrement(ref _inFlight);

            var stripe = _stripes[Environment.CurrentManagedThreadId & _stripeMask];
            lock (stripe.Lock)
            {
                if (isSuccess)
                {
                    stripe.Success++;
                    stripe.Latencies.Add(latencyMs);
                }
                else
                {
                    stripe.Failure++;
                }

                if (queueTimeMs > 0)
                {
                    stripe.QueueTimeSum += queueTimeMs;
                    stripe.QueueTimeCount++;
                    if (queueTimeMs > stripe.QueueTimeMax)
                        stripe.QueueTimeMax = queueTimeMs;
                }
            }
        }

        /// <summary>Records a scheduled item that was dropped by the MaxInFlight safety cap (k6-style dropped iteration).</summary>
        public void RecordDropped()
        {
            Interlocked.Increment(ref _dropped);
        }

        /// <summary>Records completion of one scheduling batch.</summary>
        public void BatchCompleted()
        {
            Interlocked.Increment(ref _batchesCompleted);
        }

        /// <summary>Current number of requests executing right now. Used by the engine for the MaxInFlight cap.</summary>
        public int InFlight => Volatile.Read(ref _inFlight);

        /// <summary>Total requests started so far. Used by the engine for MaxIterations accounting.</summary>
        public int Started => Volatile.Read(ref _started);

        /// <summary>Aggregates all recorded data into the final result. Call once, after the run completes.</summary>
        public LoadResult BuildResult(int workerThreadsUsed = 0)
        {
            SampleMemory();

            var elapsedSeconds = _startTimestamp == 0
                ? 0
                : (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency;

            // Merge stripes: counters plus one flat latency array for sorting.
            int success = 0, failure = 0, queueCount = 0, latencyCount = 0;
            double queueSum = 0, queueMax = 0;
            foreach (var stripe in _stripes)
            {
                lock (stripe.Lock)
                {
                    success += stripe.Success;
                    failure += stripe.Failure;
                    queueSum += stripe.QueueTimeSum;
                    queueCount += stripe.QueueTimeCount;
                    if (stripe.QueueTimeMax > queueMax)
                        queueMax = stripe.QueueTimeMax;
                    latencyCount += stripe.Latencies.Count;
                }
            }

            var latencies = new double[latencyCount];
            var offset = 0;
            foreach (var stripe in _stripes)
            {
                lock (stripe.Lock)
                {
                    stripe.Latencies.CopyTo(latencies, offset);
                    offset += stripe.Latencies.Count;
                }
            }
            Array.Sort(latencies);

            double latencySum = 0;
            foreach (var latency in latencies)
                latencySum += latency;

            var total = success + failure;
            var started = Volatile.Read(ref _started);

            return new LoadResult
            {
                ScenarioName = _scenarioName,
                Total = total,
                Success = success,
                Failure = failure,
                Time = elapsedSeconds,
                RequestsPerSecond = elapsedSeconds > 0 ? total / elapsedSeconds : 0,

                MinLatency = latencies.Length > 0 ? latencies[0] : 0,
                MaxLatency = latencies.Length > 0 ? latencies[latencies.Length - 1] : 0,
                AverageLatency = latencies.Length > 0 ? latencySum / latencies.Length : 0,
                MedianLatency = Percentile(latencies, 50),
                Percentile95Latency = Percentile(latencies, 95),
                Percentile99Latency = Percentile(latencies, 99),

                RequestsStarted = started,
                RequestsInFlight = Volatile.Read(ref _inFlight),
                Dropped = Volatile.Read(ref _dropped),
                BatchesCompleted = Volatile.Read(ref _batchesCompleted),

                AvgQueueTime = queueCount > 0 ? queueSum / queueCount : 0,
                MaxQueueTime = queueMax,

                WorkerThreadsUsed = workerThreadsUsed,
                WorkerUtilization = workerThreadsUsed > 0 && elapsedSeconds > 0
                    ? (started / (double)workerThreadsUsed) / elapsedSeconds
                    : 0,
                PeakMemoryUsage = Volatile.Read(ref _peakMemory)
            };
        }

        /// <summary>Ceiling-method percentile over a pre-sorted array (conservative estimate).</summary>
        private static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
                return 0;
            var index = (int)Math.Ceiling((percentile / 100.0) * sorted.Length) - 1;
            if (index < 0)
                index = 0;
            return sorted[Math.Min(index, sorted.Length - 1)];
        }

        private void SampleMemory()
        {
            var current = GC.GetTotalMemory(false);
            long observed;
            while (current > (observed = Volatile.Read(ref _peakMemory)))
            {
                if (Interlocked.CompareExchange(ref _peakMemory, current, observed) == observed)
                    break;
            }
        }

        private static int RoundUpToPowerOfTwo(int value)
        {
            var result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }
    }
}
