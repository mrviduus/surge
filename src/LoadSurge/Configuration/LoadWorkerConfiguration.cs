using System;

namespace LoadSurge.Configuration
{
    /// <summary>
    /// Configuration settings that control load engine behavior.
    /// Since v3.0.0 the engine is a single open-workload-model implementation
    /// (task-per-arrival); worker-pool and channel tuning options are obsolete.
    /// </summary>
    public class LoadWorkerConfiguration
    {
        /// <summary>
        /// Obsolete: since v3.0.0 there is a single engine implementation and this value is ignored.
        /// </summary>
        [Obsolete("Since v3.0.0 there is a single open-model engine; Mode is ignored.")]
        public LoadWorkerMode Mode { get; set; } = LoadWorkerMode.Hybrid;

        /// <summary>
        /// Obsolete: since v3.0.0 there is no fixed worker pool (task-per-arrival model); this value is ignored.
        /// Use MaxInFlight to bound concurrent executions.
        /// </summary>
        [Obsolete("Since v3.0.0 there is no worker pool; use MaxInFlight to bound concurrency.")]
        public int? MaxWorkerThreads { get; set; }

        /// <summary>
        /// Obsolete: since v3.0.0 the engine does not use channels; this value is ignored.
        /// </summary>
        [Obsolete("Since v3.0.0 the engine does not use channels; this value is ignored.")]
        public int? ChannelCapacity { get; set; }

        /// <summary>
        /// Safety cap on the number of concurrently executing requests (open workload model).
        /// When the cap is reached, newly scheduled iterations are dropped and counted in
        /// LoadResult.Dropped instead of executing (k6-style dropped iterations).
        /// Null (default) disables the cap - in-flight requests grow without limit,
        /// bounded only by test duration. Set this to protect the test process from
        /// memory exhaustion when the system under test hangs or responds very slowly.
        /// </summary>
        public int? MaxInFlight { get; set; }

        /// <summary>
        /// Enable detailed performance metrics collection and logging.
        /// Provides comprehensive monitoring data but may impact performance under extreme load.
        /// Useful for performance analysis and troubleshooting but should be disabled for production benchmarks.
        /// Includes per-worker statistics, queue times, and resource utilization tracking.
        /// </summary>
        public bool EnableDetailedMetrics { get; set; }

        /// <summary>
        /// Worker utilization threshold for logging warnings (0.0 to 1.0).
        /// Triggers warnings when worker efficiency falls below this percentage.
        /// Helps identify resource contention, inadequate worker pools, or system bottlenecks.
        /// Values above 0.8 (80%) indicate healthy utilization without resource starvation.
        /// </summary>
        public double WorkerUtilizationWarningThreshold { get; set; } = 0.8;

        /// <summary>
        /// Queue time threshold for logging warnings (milliseconds).
        /// Triggers warnings when work items wait longer than this duration before processing.
        /// High queue times indicate worker pool saturation or insufficient parallel capacity.
        /// Default of 1000ms (1 second) is appropriate for most load testing scenarios.
        /// </summary>
        public double QueueTimeWarningThreshold { get; set; } = 1000;
    }

    /// <summary>
    /// Available load worker implementation modes with different performance characteristics.
    /// Each mode is optimized for specific concurrency levels and resource constraints.
    /// Selection should be based on expected load patterns and system capabilities.
    /// </summary>
    public enum LoadWorkerMode
    {
        /// <summary>
        /// Original task-based implementation using .NET Task.Run for concurrent execution.
        /// Good for moderate load scenarios (less than 10k concurrent requests) with standard thread pool management.
        /// Provides simple execution model with good compatibility but limited scalability.
        /// Recommended for functional testing and moderate performance testing scenarios.
        /// </summary>
        TaskBased,

        /// <summary>
        /// Pure actor-based implementation for isolated, supervised execution scenarios.
        /// Good for fault tolerance requirements and distributed testing architectures.
        /// Provides strong isolation and supervision but may have higher overhead.
        /// Recommended when actor supervision and fault recovery are primary concerns.
        /// </summary>
        ActorBased,

        /// <summary>
        /// Hybrid channel-based implementation optimized for high-throughput scenarios.
        /// Optimal for high concurrency load testing (greater than 10k concurrent requests) with minimal overhead.
        /// Uses fixed worker pools with high-performance channels for maximum scalability.
        /// Recommended for stress testing, capacity planning, and performance benchmarking.
        /// </summary>
        Hybrid
    }
}
