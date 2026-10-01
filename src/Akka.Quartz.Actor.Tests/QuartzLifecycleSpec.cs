using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using System.Threading;
using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Quartz.Extensibility;
using Xunit;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor.Tests
{
    public class QuartzLifecycleSpec : TestKit.Xunit.TestKit
    {
        [Fact]
        public void Existing_Callback_Should_Observe_A_Running_Owned_Scheduler_And_Shut_Down_On_Stop()
        {
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCreated));
            var actor = Sys.ActorOf(Props.Create(() => new InspectingQuartzActor()));
            Watch(actor);
            var created = ExpectMsg<SchedulerCreated>(cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                Assert.True(created.WasStarted, "Existing OnSchedulerCreated overrides must still observe a running scheduler.");
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
        public async Task ActorSystem_Termination_Should_Await_Owned_Scheduler_Plugin_Shutdown()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            ControlledShutdownPlugin.Entered = new TaskCompletionSource<IScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
            ControlledShutdownPlugin.Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var system = ActorSystem.Create("quartz-shutdown-test", ConfigurationFactory.ParseString(
                "akka.coordinated-shutdown.phases.before-actor-system-terminate { timeout = 30s, recover = off }"));
            Task termination = null;
            try
            {
                var properties = new NameValueCollection
                {
                    ["quartz.plugin.controlled.type"] = typeof(ControlledShutdownPlugin).AssemblyQualifiedName
                };
                var actor = system.ActorOf(Props.Create(() => new QuartzActor(properties)));
                await actor.Ask<ActorIdentity>(new Identify(null), TimeSpan.FromSeconds(10), cancellationToken);
                termination = system.Terminate();
                var scheduler = await ControlledShutdownPlugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Assert.False(termination.IsCompleted, "System termination must await Quartz's asynchronous disposal.");
                Assert.Equal(SchedulerStatus.ShuttingDown, scheduler.Status);
                ControlledShutdownPlugin.Release.TrySetResult(true);
                await termination.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Assert.Equal(SchedulerStatus.Shutdown, scheduler.Status);
            }
            finally
            {
                ControlledShutdownPlugin.Release.TrySetResult(true);
                await (termination ?? system.Terminate()).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        [Fact]
        public void Persistent_Context_Should_Be_Installed_Even_When_Existing_Callback_Is_Overridden()
        {
            Sys.EventStream.Subscribe(TestActor, typeof(PersistentSchedulerCreated));
            var actor = Sys.ActorOf(Props.Create(() => new InspectingPersistentActor(Guid.NewGuid().ToString())));
            Watch(actor);
            var created = ExpectMsg<PersistentSchedulerCreated>(cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                Assert.Same(Sys, created.System);
                Assert.Equal(SchedulerStatus.Running, created.Scheduler.Status);
            }
            finally
            {
                Sys.Stop(actor);
                ExpectTerminated(actor, cancellationToken: TestContext.Current.CancellationToken);
                AwaitAssert(() => Assert.Equal(SchedulerStatus.Shutdown, created.Scheduler.Status), cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        private sealed record PersistentSchedulerCreated(IScheduler Scheduler, object System);
        private sealed class InspectingPersistentActor : QuartzPersistentActor
        {
            public InspectingPersistentActor(string name) : base(name) { }
            protected override void OnSchedulerCreated(IScheduler scheduler) =>
                Context.System.EventStream.Publish(new PersistentSchedulerCreated(scheduler, scheduler.Context[QuartzPersistentJob.SysKey]));
        }

        public sealed class ControlledShutdownPlugin : ISchedulerPlugin
        {
            public static TaskCompletionSource<IScheduler> Entered;
            public static TaskCompletionSource<bool> Release;
            private IScheduler _scheduler;
            public ValueTask Initialize(string name, IScheduler scheduler, CancellationToken cancellationToken)
            {
                _scheduler = scheduler;
                return ValueTask.CompletedTask;
            }
            public ValueTask Start(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public async ValueTask Shutdown(CancellationToken cancellationToken)
            {
                Entered.TrySetResult(_scheduler);
                await Release.Task;
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
