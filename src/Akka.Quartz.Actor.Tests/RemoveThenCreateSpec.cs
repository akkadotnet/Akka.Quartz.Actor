using System;
using System.Collections.Specialized;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Xunit;

namespace Akka.Quartz.Actor.Tests
{
    /// <summary>
    /// Replacing a job by sending <see cref="RemoveJob"/> and then a create command for the same keys,
    /// without waiting for <see cref="JobRemoved"/> in between.
    /// See https://github.com/akkadotnet/Akka.Quartz.Actor/issues/374
    /// </summary>
    public class RemoveThenCreateSpec : TestKit.Xunit.TestKit
    {
        private const int Rounds = 100;
        private static readonly JobKey Job = new JobKey("job");
        private static readonly TriggerKey TriggerId = new TriggerKey("trigger");

        private static ITrigger Trigger(string cron) => TriggerBuilder.Create()
            .WithIdentity(TriggerId).ForJob(Job).WithCronSchedule(cron).Build();

        [Theory]
        [InlineData("0 0 0 1 1 ?")] // idle job
        [InlineData("* * * * * ?")] // job that fires while it is being replaced
        public void QuartzActor_Should_Create_Job_Right_After_Removing_It(string cron)
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello", Trigger(cron)));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);

            for (var round = 0; round < Rounds; round++)
            {
                quartzActor.Tell(new RemoveJob(Job, TriggerId));
                quartzActor.Tell(new CreateJob(probe, "Hello", Trigger(cron)));
                ExpectMsg<JobRemoved>(cancellationToken: TestContext.Current.CancellationToken);
                ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            }

            Sys.Stop(quartzActor);
        }

        [Theory]
        [InlineData("0 0 0 1 1 ?")] // idle job
        [InlineData("* * * * * ?")] // job that fires while it is being replaced
        public void QuartzPersistentActor_Should_Create_Job_Right_After_Removing_It(string cron)
        {
            var probe = CreateTestProbe(Sys);
            var props = new NameValueCollection { [QuartzActor.PropertySchedulerInstanceName] = Guid.NewGuid().ToString() };
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor(props)), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello", Trigger(cron)));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);

            for (var round = 0; round < Rounds; round++)
            {
                quartzActor.Tell(new RemoveJob(Job, TriggerId));
                quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello", Trigger(cron)));
                ExpectMsg<JobRemoved>(cancellationToken: TestContext.Current.CancellationToken);
                ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            }

            Sys.Stop(quartzActor);
        }
    }
}
