using System;
using System.Threading;
using System.Threading.Tasks;

// Define namespace for load testing data models and configuration structures
// Contains all DTOs and data contracts used throughout the load testing framework
namespace LoadSurge.Models
{
    /// <summary>
    /// Defines a complete load test execution plan containing test configuration and action.
    /// This class encapsulates all information needed to execute a load test scenario.
    /// Serves as the primary contract between test definition and execution infrastructure.
    /// Immutable configuration that drives all aspects of load test execution.
    /// </summary>
    public class LoadExecutionPlan
    {
        /// <summary>
        /// Gets or sets the unique name identifier for this load test scenario.
        /// Used for logging, reporting, and result identification purposes.
        /// This name appears in all log messages and result files for traceability.
        /// Should be descriptive and unique within the test suite for clarity.
        /// </summary>
        public string Name { get; set; } = string.Empty;
        
        /// <summary>
        /// Gets or sets the load test configuration settings including concurrency, duration, and intervals.
        /// Defines how the load test should be executed in terms of timing and scale.
        /// Contains all parameters that control the test execution pattern and resource utilization.
        /// This configuration drives the worker creation and scheduling algorithms.
        /// </summary>
        public LoadSettings Settings { get; set; } = new LoadSettings();
        
        /// <summary>
        /// Gets or sets the asynchronous test action to be executed during load testing.
        /// Returns true for successful execution, false for failure - used for success rate calculations.
        /// This function represents the actual workload that will be subjected to load testing.
        /// Should be idempotent and thread-safe as it will be executed concurrently by multiple workers.
        /// Performance of this action directly impacts the overall test results and metrics.
        /// </summary>
        public Func<Task<bool>>? Action { get; set; }

        /// <summary>
        /// Gets or sets the cancellation-aware test action, preferred over <see cref="Action"/>.
        /// The provided token is cancelled when the per-request <see cref="LoadSettings.RequestTimeout"/>
        /// elapses or when the caller cancels the test run, allowing the action to abort promptly
        /// instead of leaking work in the background.
        /// Exactly one of <see cref="Action"/> or <see cref="ActionWithCancellation"/> must be set.
        /// </summary>
        public Func<CancellationToken, Task<bool>>? ActionWithCancellation { get; set; }
    }
}