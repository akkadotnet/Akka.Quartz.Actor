using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Xunit;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor.Tests
{
    public class QuartzLifecycleSpec : TestKit.Xunit.TestKit
    {
        [Fact]
        public void Owned_Scheduler_Should_Be_Initialized_Before_Starting_And_Shut_Down_On_Stop()
        {
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCreated));
            var actor = Sys.ActorOf(Props.Create(() => new InspectingQuartzActor()));
            Watch(actor);
            var created = ExpectMsg<SchedulerCreated>(cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                Assert.False(created.WasStarted, "Recovery must not fire jobs before OnSchedulerCreated has initialized their context.");
                actor.Tell(new Identify(null));
                ExpectMsg<ActorIdentity>(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(SchedulerStatus.Running, created.Scheduler.Status);
            }
            finally
            {
                Sys.Stop(actor);
                ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
                AwaitAssert(() => Assert.Equal(SchedulerStatus.Shutdown, created.Scheduler.Status), cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task Supplied_Scheduler_Should_Remain_Usable_After_Actor_Stop()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var factory = QuartzSchedulerBuilder.Create().Build();
            var scheduler = await factory.GetScheduler(cancellationToken);
            await scheduler.Start(cancellationToken);
            var actor = Sys.ActorOf(Props.Create(() => new QuartzActor(scheduler)));
            Watch(actor);
            actor.Tell(new Identify(null));
            ExpectMsg<ActorIdentity>(cancellationToken: cancellationToken);
            Sys.Stop(actor);
            ExpectTerminated(actor, cancellationToken: cancellationToken);

            Assert.NotEqual(SchedulerStatus.Shutdown, scheduler.Status);
            var job = QuartzJob.CreateBuilderWithData(TestActor, "still usable").Build();
            await scheduler.ScheduleJob(job, TriggerBuilder.Create().StartNow().Build(), new ScheduleJobOptions(), cancellationToken);
            ExpectMsg("still usable", cancellationToken: cancellationToken);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Invalid_Recipient_Should_Produce_One_Failure_And_No_Saved_Job(bool persistent)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await using var factory = QuartzSchedulerBuilder.Create().Build();
            var scheduler = await factory.GetScheduler(cancellationToken);
            var actor = persistent
                ? Sys.ActorOf(Props.Create(() => new QuartzPersistentActor(scheduler)))
                : Sys.ActorOf(Props.Create(() => new QuartzActor(scheduler)));
            var key = new JobKey("invalid-recipient");
            var trigger = TriggerBuilder.Create().WithIdentity("invalid-trigger").ForJob(key).StartNow().Build();
            actor.Tell(persistent ? (object)new CreatePersistentJob(null, "message", trigger) : new CreateJob(null, "message", trigger));

            var failure = ExpectMsg<CreateJobFail>(cancellationToken: cancellationToken);
            Assert.IsType<ArgumentNullException>(failure.Reason);
            // The identity reply is a mailbox barrier: any second response to the command is unexpected.
            actor.Tell(new Identify(null));
            ExpectMsg<ActorIdentity>(cancellationToken: cancellationToken);
            Assert.False(await scheduler.Exists(key, cancellationToken));
        }

        private sealed record SchedulerCreated(IScheduler Scheduler, bool WasStarted);

        private sealed class InspectingQuartzActor : QuartzActor
        {
            protected override void OnSchedulerCreated(IScheduler scheduler)
            {
                Context.System.EventStream.Publish(new SchedulerCreated(scheduler, scheduler.Status == SchedulerStatus.Running));
            }
        }
    }
}
