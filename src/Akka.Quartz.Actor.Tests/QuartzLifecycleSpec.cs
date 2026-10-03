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
            public InspectingPersistentActor(string name)
                : base(new NameValueCollection { [PropertySchedulerInstanceName] = name }) { }
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Stop_During_Async_Initialization_Or_Startup_Should_Still_Dispose_The_Scheduler(bool blockStart)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            ControlledStartupPlugin.Entered = new TaskCompletionSource<IScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
            ControlledStartupPlugin.Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ControlledStartupPlugin.Stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var system = ActorSystem.Create("quartz-startup-stop-test", ConfigurationFactory.ParseString(
                "akka.coordinated-shutdown.phases.before-actor-system-terminate { timeout = 20s, recover = off }"));
            Task termination = null;
            try
            {
                var properties = new NameValueCollection
                {
                    ["quartz.plugin.waiting.type"] = typeof(ControlledStartupPlugin).AssemblyQualifiedName,
                    ["quartz.plugin.waiting.blockStart"] = blockStart.ToString()
                };
                var actor = system.ActorOf(Props.Create(() => new QuartzActor(properties)));
                var died = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                system.ActorOf(Props.Create(() => new TerminationObserver(actor, died)));
                var scheduler = await ControlledStartupPlugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                system.Stop(actor);
                await died.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                termination = system.Terminate();
                Assert.False(termination.IsCompleted);
                ControlledStartupPlugin.Release.TrySetResult(true);
                await termination.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Assert.True(ControlledStartupPlugin.Stopped.Task.IsCompletedSuccessfully);
                Assert.Equal(SchedulerStatus.Shutdown, scheduler.Status);
            }
            finally
            {
                ControlledStartupPlugin.Release.TrySetResult(true);
                await (termination ?? system.Terminate()).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        private sealed class TerminationObserver : ReceiveActor
        {
            public TerminationObserver(IActorRef target, TaskCompletionSource<bool> died)
            {
                Context.Watch(target);
                Receive<Terminated>(_ => died.TrySetResult(true));
            }
        }

        public sealed class ControlledStartupPlugin : ISchedulerPlugin
        {
            public static TaskCompletionSource<IScheduler> Entered;
            public static TaskCompletionSource<bool> Release;
            public static TaskCompletionSource<bool> Stopped;
            public bool BlockStart { get; set; }
            private IScheduler _scheduler;
            public async ValueTask Initialize(string name, IScheduler scheduler, CancellationToken cancellationToken)
            {
                _scheduler = scheduler;
                if (!BlockStart) await BlockAsync();
            }
            public async ValueTask Start(CancellationToken cancellationToken)
            {
                if (BlockStart) await BlockAsync();
            }
            private async Task BlockAsync()
            {
                Entered.TrySetResult(_scheduler);
                await Release.Task;
            }
            public ValueTask Shutdown(CancellationToken cancellationToken)
            {
                Stopped.TrySetResult(true);
                return ValueTask.CompletedTask;
            }
        }

        [Fact]
        public async Task Restarted_Actor_Should_Not_Create_A_Scheduler_Until_The_Previous_Incarnation_Has_Shut_Down()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            DrainingPlugin.Reset();
            var properties = new NameValueCollection
            {
                [QuartzActor.PropertySchedulerInstanceName] = "restart-" + Guid.NewGuid(),
                ["quartz.plugin.draining.type"] = typeof(DrainingPlugin).AssemblyQualifiedName
            };
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCreated));
            var actor = Sys.ActorOf(Props.Create(() => new CrashingQuartzActor(properties)));
            Watch(actor);
            var first = ExpectMsg<SchedulerCreated>(cancellationToken: cancellationToken);
            try
            {
                actor.Tell(CrashingQuartzActor.Crash);
                var draining = await DrainingPlugin.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Assert.Same(first.Scheduler, draining);

                // Same instance name and job store: a second scheduler must not exist while the first still runs jobs.
                ExpectNoMsg(TimeSpan.FromSeconds(1), cancellationToken);
                Assert.Equal(1, DrainingPlugin.InitializedCount);

                DrainingPlugin.Release.TrySetResult(true);
                var second = ExpectMsg<SchedulerCreated>(cancellationToken: cancellationToken);
                Assert.NotSame(first.Scheduler, second.Scheduler);
                Assert.Equal(SchedulerStatus.Shutdown, first.Scheduler.Status);
                Assert.Equal(2, DrainingPlugin.InitializedCount);
            }
            finally
            {
                DrainingPlugin.Release.TrySetResult(true);
                Sys.Stop(actor);
                ExpectTerminated(actor, cancellationToken: cancellationToken);
            }
        }

        [Fact]
        public async Task Failed_Owned_Scheduler_Shutdown_Should_Not_Block_A_Restart_Or_Fail_Coordinated_Shutdown()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            ThrowingShutdownPlugin.Reset();
            var system = ActorSystem.Create("quartz-failed-shutdown-test", ConfigurationFactory.ParseString(
                "akka.coordinated-shutdown.phases.before-actor-system-terminate { timeout = 10s, recover = off }"));
            Task termination = null;
            try
            {
                var properties = new NameValueCollection
                {
                    [QuartzActor.PropertySchedulerInstanceName] = "failed-shutdown-" + Guid.NewGuid(),
                    ["quartz.plugin.throwing.type"] = typeof(ThrowingShutdownPlugin).AssemblyQualifiedName
                };
                var probe = CreateTestProbe(system);
                system.EventStream.Subscribe(probe, typeof(SchedulerCreated));
                var actor = system.ActorOf(Props.Create(() => new CrashingQuartzActor(properties)));
                var first = probe.ExpectMsg<SchedulerCreated>(cancellationToken: cancellationToken);

                actor.Tell(CrashingQuartzActor.Crash);
                // The restarted incarnation waits for the failed shutdown, so its arrival proves that the failed
                // entry has been released rather than left registered for coordinated shutdown to await again.
                var second = probe.ExpectMsg<SchedulerCreated>(TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
                Assert.NotSame(first.Scheduler, second.Scheduler);
                Assert.Equal(1, ThrowingShutdownPlugin.Failures);

                termination = system.Terminate();
                await termination.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Assert.Equal(SchedulerStatus.Shutdown, second.Scheduler.Status);
                Assert.Equal(CoordinatedShutdown.ActorSystemTerminateReason.Instance, CoordinatedShutdown.Get(system).ShutdownReason);
            }
            finally
            {
                await (termination ?? system.Terminate()).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        public sealed class ThrowingShutdownPlugin : ISchedulerPlugin
        {
            private static int _failures;
            public static int Failures => Volatile.Read(ref _failures);

            public static void Reset() => _failures = 0;

            public ValueTask Initialize(string name, IScheduler scheduler, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask Start(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            // Only the first scheduler fails, so the restarted one can shut down normally.
            public ValueTask Shutdown(CancellationToken cancellationToken) =>
                Interlocked.Increment(ref _failures) == 1
                    ? throw new InvalidOperationException("Simulated plugin shutdown failure.")
                    : ValueTask.CompletedTask;
        }

        private sealed class CrashingQuartzActor : QuartzActor
        {
            public const string Crash = "crash";

            public CrashingQuartzActor(NameValueCollection properties) : base(properties) { }

            protected override bool Receive(object message)
            {
                if (Crash.Equals(message)) throw new InvalidOperationException("Simulated failure to force a supervised restart.");
                return base.Receive(message);
            }

            protected override void OnSchedulerCreated(IScheduler scheduler) =>
                Context.System.EventStream.Publish(new SchedulerCreated(scheduler, scheduler.Status == SchedulerStatus.Running));
        }

        public sealed class DrainingPlugin : ISchedulerPlugin
        {
            private static int _initialized;
            public static TaskCompletionSource<IScheduler> ShutdownEntered;
            public static TaskCompletionSource<bool> Release;
            public static int InitializedCount => Volatile.Read(ref _initialized);
            private IScheduler _scheduler;

            public static void Reset()
            {
                _initialized = 0;
                ShutdownEntered = new TaskCompletionSource<IScheduler>(TaskCreationOptions.RunContinuationsAsynchronously);
                Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public ValueTask Initialize(string name, IScheduler scheduler, CancellationToken cancellationToken)
            {
                _scheduler = scheduler;
                Interlocked.Increment(ref _initialized);
                return ValueTask.CompletedTask;
            }

            public ValueTask Start(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            // Stands in for jobs that are still running when the actor fails.
            public async ValueTask Shutdown(CancellationToken cancellationToken)
            {
                ShutdownEntered.TrySetResult(_scheduler);
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
