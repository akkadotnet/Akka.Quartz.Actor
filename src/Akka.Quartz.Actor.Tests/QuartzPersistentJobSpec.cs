using System;
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
    }
}
