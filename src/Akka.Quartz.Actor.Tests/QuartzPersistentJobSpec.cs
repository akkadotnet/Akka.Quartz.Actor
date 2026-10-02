using System;
using System.Threading;
using System.Threading.Tasks;
using Quartz;
using Xunit;

namespace Akka.Quartz.Actor.Tests
{
    public class QuartzPersistentJobSpec : TestKit.Xunit.TestKit
    {
        [Fact]
        public async Task QuartzPersistentJob_Should_Deliver_Legacy_Byte_Array_Message()
        {
            var probe = CreateTestProbe(Sys);
            var messageBytes = Sys.Serialization.FindSerializerForType(typeof(object)).ToBinary("legacy message");
            var data = new JobDataMap
            {
                ["actor"] = probe.Ref.Path.ToSerializationFormat(),
                ["message"] = messageBytes
            };

            await using var factory = QuartzSchedulerBuilder.Create().Build();
            var scheduler = await factory.GetScheduler(TestContext.Current.CancellationToken);
            scheduler.Context.Add(QuartzPersistentJob.SysKey, Sys);
            await scheduler.Start(TestContext.Current.CancellationToken);

            var job = JobBuilder.Create<QuartzPersistentJob>().UsingJobData(data).Build();
            var trigger = TriggerBuilder.Create().StartNow().Build();
            await scheduler.ScheduleJob(job, trigger, new ScheduleJobOptions(), TestContext.Current.CancellationToken);

            probe.ExpectMsg("legacy message", TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        }

        [Theory]
        [InlineData(42)]
        [InlineData("not base64!")]
        public async Task QuartzPersistentJob_Should_Fail_Unreadable_Message(object storedMessage)
        {
            var probe = CreateTestProbe(Sys);
            var data = new JobDataMap
            {
                ["actor"] = probe.Ref.Path.ToSerializationFormat(),
                ["message"] = storedMessage
            };

            await using var factory = QuartzSchedulerBuilder.Create().Build();
            var scheduler = await factory.GetScheduler(TestContext.Current.CancellationToken);
            scheduler.Context.Add(QuartzPersistentJob.SysKey, Sys);
            var job = JobBuilder.Create<QuartzPersistentJob>().UsingJobData(data).Build();
            var listener = new ExecutionListener();
            scheduler.ListenerManager.AddJobListener(listener, new[] { Matchers.AllJobs() });
            await scheduler.Start(TestContext.Current.CancellationToken);

            var trigger = TriggerBuilder.Create().StartNow().Build();
            await scheduler.ScheduleJob(job, trigger, new ScheduleJobOptions(), TestContext.Current.CancellationToken);

            var failure = await listener.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(failure);
            Assert.Contains(job.Key.ToString(), failure.Message);
            probe.ExpectNoMsg(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }

        private sealed class ExecutionListener : IJobListener
        {
            public TaskCompletionSource<JobExecutionException> Executed { get; } =
                new TaskCompletionSource<JobExecutionException>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Name => nameof(ExecutionListener);

            public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default) => default;

            public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default) => default;

            public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException jobException, CancellationToken cancellationToken = default)
            {
                Executed.TrySetResult(jobException);
                return default;
            }
        }
    }
}
