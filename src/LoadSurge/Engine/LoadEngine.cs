using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LoadSurge.Configuration;
using LoadSurge.Models;

namespace LoadSurge.Engine
{
    /// <summary>
    /// Open-workload-model load engine (constant arrival rate, NBomber Inject / k6 arrival-rate style).
    /// A scheduler loop injects Concurrency iterations every Interval at absolute-time boundaries
    /// (no drift), regardless of whether previous responses have returned - in-flight requests
    /// accumulate under a slow system, which is exactly what an open model must measure.
    /// Each iteration is a task-per-arrival on the thread pool; there is no worker pool that
    /// would silently convert the open model into a closed one.
    /// The optional MaxInFlight cap drops excess iterations and counts them (k6 dropped_iterations).
    /// </summary>
    internal static class LoadEngine
    {
        private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// Executes the plan and returns aggregated results.
        /// Cancellation stops scheduling, cancels in-flight cancellation-aware actions,
        /// and returns the partial results collected so far.
        /// </summary>
        public static async Task<LoadResult> RunAsync(
            LoadExecutionPlan plan,
            LoadWorkerConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var settings = plan.Settings;
            var collector = new MetricsCollector(plan.Name);
            collector.MarkStarted();

            var runStart = Stopwatch.GetTimestamp();
            var executed = 0;   // iterations actually spawned (dropped ones do not consume MaxIterations budget)
            var batchNumber = 0;
            var maxInFlight = configuration.MaxInFlight;
            var durationMs = settings.Duration.TotalMilliseconds;
            var intervalMs = settings.Interval.TotalMilliseconds;
            var stoppedByMaxIterations = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - runStart) * TimestampToMs;
                var expectedBatchStartMs = batchNumber * intervalMs;

                if (settings.MaxIterations.HasValue && executed >= settings.MaxIterations.Value)
                {
                    stoppedByMaxIterations = true;
                    break;
                }

                // CompleteCurrentInterval schedules every batch whose slot begins within Duration;
                // Duration and StrictDuration stop as soon as elapsed time runs out.
                var shouldStop = settings.TerminationMode == TerminationMode.CompleteCurrentInterval
                    ? expectedBatchStartMs >= durationMs
                    : elapsedMs >= durationMs;
                if (shouldStop)
                    break;

                var itemsThisBatch = settings.Concurrency;
                if (settings.MaxIterations.HasValue)
                    itemsThisBatch = Math.Min(itemsThisBatch, settings.MaxIterations.Value - executed);

                for (var i = 0; i < itemsThisBatch; i++)
                {
                    if (maxInFlight.HasValue && collector.InFlight >= maxInFlight.Value)
                    {
                        collector.RecordDropped();
                        continue;
                    }

                    var scheduledAt = Stopwatch.GetTimestamp();
                    // CancellationToken.None is deliberate: a spawned iteration must always run
                    // and record its outcome; run cancellation reaches the action via the token
                    // passed into ExecuteOneAsync instead.
                    _ = Task.Run(() => ExecuteOneAsync(plan, collector, scheduledAt, cancellationToken), CancellationToken.None);
                    executed++;
                }

                collector.BatchCompleted();
                batchNumber++;

                // Absolute-time pacing: sleep until the next batch slot, immune to scheduling drift.
                var nextBatchMs = batchNumber * intervalMs;
                var delayMs = nextBatchMs - (Stopwatch.GetTimestamp() - runStart) * TimestampToMs;
                if (delayMs > 0 && !await DelayAsync(delayMs, cancellationToken).ConfigureAwait(false))
                    break;
            }

            // The run spans the full Duration window so Time/RPS are schedule-normalized
            // (matches pre-3.0 behavior). MaxIterations and cancellation complete early.
            // Loop because Task.Delay may wake marginally early relative to Stopwatch.
            if (!stoppedByMaxIterations
                && settings.TerminationMode != TerminationMode.StrictDuration
                && !cancellationToken.IsCancellationRequested)
            {
                while (true)
                {
                    var remainingMs = durationMs - (Stopwatch.GetTimestamp() - runStart) * TimestampToMs;
                    if (remainingMs <= 0 || !await DelayAsync(remainingMs, cancellationToken).ConfigureAwait(false))
                        break;
                }
            }

            // Graceful drain: give in-flight requests up to the grace period to finish.
            // StrictDuration and cancellation skip the drain and report whatever is still in flight
            // (cancellation-aware actions have been signalled and unwind on their own).
            if (settings.TerminationMode != TerminationMode.StrictDuration
                && !cancellationToken.IsCancellationRequested)
            {
                var graceMs = settings.EffectiveGracefulStopTimeout.TotalMilliseconds;
                var drainStart = Stopwatch.GetTimestamp();
                while (collector.InFlight > 0
                    && (Stopwatch.GetTimestamp() - drainStart) * TimestampToMs < graceMs)
                {
                    if (!await DelayAsync(10, cancellationToken).ConfigureAwait(false))
                        break;
                }
            }

            return collector.BuildResult();
        }

        /// <summary>Delays without throwing; returns false when cancelled so callers can break out.</summary>
        private static async Task<bool> DelayAsync(double milliseconds, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Runs a single iteration: measures queue lag (schedule to start) and execution latency,
        /// applies the optional per-request timeout, and records the outcome. Never throws.
        /// </summary>
        private static async Task ExecuteOneAsync(
            LoadExecutionPlan plan,
            MetricsCollector collector,
            long scheduledAt,
            CancellationToken cancellationToken)
        {
            collector.RequestStarted();
            var startedAt = Stopwatch.GetTimestamp();
            var queueTimeMs = (startedAt - scheduledAt) * TimestampToMs;

            try
            {
                var timeout = plan.Settings.RequestTimeout;
                bool isSuccess;

                if (plan.ActionWithCancellation != null)
                {
                    // Cancellation-aware path: the action observes both run cancellation and
                    // the per-request timeout, so no work leaks past either signal.
                    using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        if (timeout.HasValue)
                            requestCts.CancelAfter(timeout.Value);
                        isSuccess = await plan.ActionWithCancellation(requestCts.Token).ConfigureAwait(false);
                    }
                }
                else
                {
                    isSuccess = timeout.HasValue
                        ? await ExecuteWithTimeoutAsync(plan.Action!, timeout.Value).ConfigureAwait(false)
                        : await plan.Action!().ConfigureAwait(false);
                }

                var latencyMs = (Stopwatch.GetTimestamp() - startedAt) * TimestampToMs;
                collector.RecordResult(isSuccess, latencyMs, queueTimeMs);
            }
            catch
            {
                // A throwing action (including OperationCanceledException on timeout/cancel)
                // is a failure; latency of failures is excluded from stats anyway.
                collector.RecordResult(false, 0, queueTimeMs);
            }
        }

        /// <summary>
        /// Races the legacy (token-less) action against a timeout. On timeout the iteration is
        /// recorded as a failure; the underlying task keeps running unobserved, so its eventual
        /// fault is observed to avoid UnobservedTaskException. Prefer ActionWithCancellation,
        /// which aborts the work itself instead of leaking it.
        /// </summary>
        private static async Task<bool> ExecuteWithTimeoutAsync(Func<Task<bool>> action, TimeSpan timeout)
        {
            var actionTask = action();
            using (var timeoutCts = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(timeout, timeoutCts.Token);
                var winner = await Task.WhenAny(actionTask, delayTask).ConfigureAwait(false);
                if (winner == actionTask)
                {
                    timeoutCts.Cancel(); // release the timer
                    return await actionTask.ConfigureAwait(false);
                }

                _ = actionTask.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return false;
            }
        }
    }
}
