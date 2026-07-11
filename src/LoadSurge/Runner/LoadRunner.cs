using System;
using System.Threading;
using System.Threading.Tasks;
using LoadSurge.Configuration;
using LoadSurge.Engine;
using LoadSurge.Models;

namespace LoadSurge.Runner
{
	/// <summary>
	/// Main entry point for executing load tests.
	/// Runs the open-workload-model engine (constant arrival rate): iterations are injected
	/// on schedule regardless of response times, so in-flight requests accumulate under a
	/// slow system - exactly what a load test must measure.
	/// </summary>
	public static class LoadRunner
	{
		/// <summary>
		/// Executes a load test with default configuration settings.
		/// </summary>
		/// <param name="executionPlan">The load test execution plan containing test action and settings</param>
		/// <returns>Aggregated load test results including performance metrics</returns>
		public static Task<LoadResult> Run(LoadExecutionPlan executionPlan)
		{
			return Run(executionPlan, null, CancellationToken.None);
		}

		/// <summary>
		/// Executes a load test with specified configuration settings.
		/// </summary>
		/// <param name="executionPlan">The load test execution plan containing test action and settings</param>
		/// <param name="configuration">Optional configuration for engine behavior (e.g. MaxInFlight safety cap)</param>
		/// <returns>Aggregated load test results with detailed performance metrics</returns>
		public static Task<LoadResult> Run(
			LoadExecutionPlan executionPlan,
			LoadWorkerConfiguration? configuration = null)
		{
			return Run(executionPlan, configuration, CancellationToken.None);
		}

		/// <summary>
		/// Executes a load test with specified configuration settings and cancellation support.
		/// Cancelling stops scheduling new iterations, cancels in-flight cancellation-aware actions,
		/// and returns the partial results collected so far (it does not throw
		/// <see cref="OperationCanceledException"/> - partial data is the point of a load test).
		/// </summary>
		/// <param name="executionPlan">The load test execution plan containing test action and settings</param>
		/// <param name="configuration">Optional configuration for engine behavior (e.g. MaxInFlight safety cap)</param>
		/// <param name="cancellationToken">Token to stop the run early</param>
		/// <returns>Aggregated load test results with detailed performance metrics</returns>
		public static async Task<LoadResult> Run(
			LoadExecutionPlan executionPlan,
			LoadWorkerConfiguration? configuration,
			CancellationToken cancellationToken)
		{
			Validate(executionPlan, configuration);

			configuration ??= new LoadWorkerConfiguration();

			return await LoadEngine.RunAsync(executionPlan, configuration, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Validates the execution plan and configuration, failing fast with actionable messages.
		/// </summary>
		private static void Validate(LoadExecutionPlan executionPlan, LoadWorkerConfiguration? configuration)
		{
			if (executionPlan == null)
				throw new ArgumentNullException(nameof(executionPlan));

			if (executionPlan.Action == null && executionPlan.ActionWithCancellation == null)
				throw new ArgumentNullException(nameof(executionPlan),
					"Set either LoadExecutionPlan.Action or LoadExecutionPlan.ActionWithCancellation.");

			if (executionPlan.Action != null && executionPlan.ActionWithCancellation != null)
				throw new ArgumentException(
					"Set only one of LoadExecutionPlan.Action or LoadExecutionPlan.ActionWithCancellation, not both.",
					nameof(executionPlan));

			var settings = executionPlan.Settings;
			if (settings == null)
				throw new ArgumentNullException(nameof(executionPlan), "LoadExecutionPlan.Settings must not be null.");

			if (settings.Concurrency <= 0)
				throw new ArgumentOutOfRangeException(nameof(executionPlan), settings.Concurrency,
					"LoadSettings.Concurrency must be at least 1.");

			if (settings.Duration <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(executionPlan), settings.Duration,
					"LoadSettings.Duration must be positive.");

			if (settings.Interval <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(executionPlan), settings.Interval,
					"LoadSettings.Interval must be positive; a zero interval would spin the scheduler at 100% CPU.");

			if (settings.MaxIterations.HasValue && settings.MaxIterations.Value <= 0)
				throw new ArgumentOutOfRangeException(nameof(executionPlan), settings.MaxIterations,
					"LoadSettings.MaxIterations must be at least 1 when set.");

			if (settings.RequestTimeout.HasValue && settings.RequestTimeout.Value <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(executionPlan), settings.RequestTimeout,
					"LoadSettings.RequestTimeout must be positive when set.");

			if (settings.GracefulStopTimeout.HasValue && settings.GracefulStopTimeout.Value < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(executionPlan), settings.GracefulStopTimeout,
					"LoadSettings.GracefulStopTimeout cannot be negative.");

			if (configuration?.MaxInFlight is int maxInFlight && maxInFlight <= 0)
				throw new ArgumentOutOfRangeException(nameof(configuration), maxInFlight,
					"LoadWorkerConfiguration.MaxInFlight must be at least 1 when set.");
		}
	}
}
