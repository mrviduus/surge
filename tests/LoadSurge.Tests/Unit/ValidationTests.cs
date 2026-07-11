using System;
using System.Threading.Tasks;
using Xunit;
using LoadSurge.Configuration;
using LoadSurge.Models;
using LoadSurge.Runner;

namespace LoadSurge.Tests.Unit
{
    /// <summary>
    /// Tests for LoadRunner input validation - fail fast with actionable messages
    /// instead of spinning the scheduler or silently doing nothing.
    /// </summary>
    public class ValidationTests
    {
        private static LoadExecutionPlan ValidPlan() => new LoadExecutionPlan
        {
            Name = "valid",
            Settings = new LoadSettings
            {
                Concurrency = 1,
                Duration = TimeSpan.FromSeconds(1),
                Interval = TimeSpan.FromMilliseconds(100)
            },
            Action = () => Task.FromResult(true)
        };

        [Fact]
        public async Task Null_Plan_Throws()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => LoadRunner.Run(null!));
        }

        [Fact]
        public async Task Missing_Action_Throws()
        {
            var plan = ValidPlan();
            plan.Action = null;

            await Assert.ThrowsAsync<ArgumentNullException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Both_Actions_Set_Throws()
        {
            var plan = ValidPlan();
            plan.ActionWithCancellation = _ => Task.FromResult(true);

            await Assert.ThrowsAsync<ArgumentException>(() => LoadRunner.Run(plan));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task NonPositive_Concurrency_Throws(int concurrency)
        {
            var plan = ValidPlan();
            plan.Settings.Concurrency = concurrency;

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Zero_Duration_Throws()
        {
            var plan = ValidPlan();
            plan.Settings.Duration = TimeSpan.Zero;

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Zero_Interval_Throws_Instead_Of_Spinning()
        {
            // Interval=0 previously spun the scheduler at 100% CPU with unbounded injection.
            var plan = ValidPlan();
            plan.Settings.Interval = TimeSpan.Zero;

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Zero_MaxIterations_Throws()
        {
            var plan = ValidPlan();
            plan.Settings.MaxIterations = 0;

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Zero_RequestTimeout_Throws()
        {
            var plan = ValidPlan();
            plan.Settings.RequestTimeout = TimeSpan.Zero;

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Negative_GracefulStopTimeout_Throws()
        {
            var plan = ValidPlan();
            plan.Settings.GracefulStopTimeout = TimeSpan.FromSeconds(-1);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(plan));
        }

        [Fact]
        public async Task Zero_MaxInFlight_Throws()
        {
            var config = new LoadWorkerConfiguration { MaxInFlight = 0 };

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LoadRunner.Run(ValidPlan(), config));
        }

        [Fact]
        public async Task Valid_Plan_With_Cancellable_Action_Runs()
        {
            var plan = ValidPlan();
            plan.Action = null;
            plan.ActionWithCancellation = _ => Task.FromResult(true);

            var result = await LoadRunner.Run(plan);

            Assert.True(result.Total > 0);
            Assert.Equal(result.Total, result.Success);
        }
    }
}
