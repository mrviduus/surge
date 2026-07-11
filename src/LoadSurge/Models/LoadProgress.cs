using System;

namespace LoadSurge.Models
{
    /// <summary>
    /// Point-in-time snapshot of a running load test, delivered via
    /// <see cref="Configuration.LoadWorkerConfiguration.Progress"/> at
    /// <see cref="Configuration.LoadWorkerConfiguration.ProgressInterval"/> cadence.
    /// Turns a long run from a black box into a live feed.
    /// </summary>
    public class LoadProgress
    {
        /// <summary>Gets or sets seconds elapsed since the run started.</summary>
        public double ElapsedSeconds { get; set; }

        /// <summary>Gets or sets the total requests started so far.</summary>
        public int RequestsStarted { get; set; }

        /// <summary>Gets or sets the completed requests so far (success + failure).</summary>
        public int Completed { get; set; }

        /// <summary>Gets or sets the successful requests so far.</summary>
        public int Success { get; set; }

        /// <summary>Gets or sets the failed requests so far.</summary>
        public int Failure { get; set; }

        /// <summary>Gets or sets the requests currently executing.</summary>
        public int InFlight { get; set; }

        /// <summary>Gets or sets the iterations dropped by the MaxInFlight cap so far.</summary>
        public int Dropped { get; set; }

        /// <summary>Gets or sets the average completed-requests-per-second since the run started.</summary>
        public double RequestsPerSecond { get; set; }
    }
}
