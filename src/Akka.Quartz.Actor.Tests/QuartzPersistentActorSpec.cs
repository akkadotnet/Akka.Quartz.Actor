using System;
using System.Threading;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Akka.Quartz.Actor.Exceptions;
using Quartz;
using Xunit;

namespace Akka.Quartz.Actor.Tests
{
    public class QuartzPersistentActorSpec : TestKit.Xunit2.TestKit
    {
        [Fact]
        public void QuartzPersistentActor_Should_Create_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello", TriggerBuilder.Create().WithCronSchedule("*0/10 * * * * ?").Build()));
            ExpectMsg<JobCreated>();
            probe.ExpectMsg("Hello", TimeSpan.FromSeconds(11));
            Thread.Sleep(TimeSpan.FromSeconds(10));
            probe.ExpectMsg("Hello");
            Sys.Stop(quartzActor);            
        }

        [Fact]
        public void QuartzPersistentActor_Should_Remove_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello remove", TriggerBuilder.Create().WithCronSchedule("0/10 * * * * ?").Build()));
            var jobCreated = ExpectMsg<JobCreated>();
            probe.ExpectMsg("Hello remove", TimeSpan.FromSeconds(11));
            quartzActor.Tell(new RemoveJob(jobCreated.JobKey, jobCreated.TriggerKey));
            ExpectMsg<JobRemoved>();
            Thread.Sleep(TimeSpan.FromSeconds(10));
            probe.ExpectNoMsg(TimeSpan.FromSeconds(10));
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzPersistentActor_Should_Fail_With_Null_Trigger()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello", null));
            var failedJob = ExpectMsg<CreateJobFail>();
            Assert.NotNull(failedJob.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzPersistentActor_Should_Fail_With_Null_Actor()
        {
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(null, "Hello", TriggerBuilder.Create().WithCronSchedule(" * * * * * ?").Build()));
            var failedJob = ExpectMsg<CreateJobFail>();
            Assert.NotNull(failedJob.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzPersistentActor_Should_Not_Remove_UnExisting_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            quartzActor.Tell(new RemoveJob(new JobKey("key"), new TriggerKey("key")));
            var failure=ExpectMsg<RemoveJobFail>();
            Assert.IsType<JobNotFoundException>(failure.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzPersistentActor_Should_Handle_New_Incarnations()
        {
            // setup first system
            var firstSystem = ActorSystem.Create("test", DefaultConfig);
            var firstProbe = CreateTestProbe(firstSystem);
            var firstIncarnation = firstSystem.ActorOf(Props.Create(() => new Relaying(firstProbe)), "relay");
            Watch(firstIncarnation);

            var firstQuartz = firstSystem.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            firstQuartz.Tell(new CreatePersistentJob(firstIncarnation.Path, "Hello", TriggerBuilder.Create().WithCronSchedule("0/2 * * * * ?").Build()));
            ExpectMsg<JobCreated>();
            firstProbe.ExpectMsg("Hello", TimeSpan.FromSeconds(10));
            firstSystem.Stop(firstIncarnation);
            ExpectTerminated(firstIncarnation, TimeSpan.FromSeconds(10));

            // simulate a restart
            // use the same quartz scheduler which simulates retrieval from quartz jobstore
            var secondSystem = ActorSystem.Create("test", DefaultConfig);
            secondSystem.ActorOf(Props.Create(() => new QuartzPersistentActor()), "QuartzActor");
            var secondProbe = CreateTestProbe(secondSystem);
            var secondIncarnation = secondSystem.ActorOf(Props.Create(() => new Relaying(secondProbe)), "relay");
            secondProbe.ExpectMsgFrom(secondIncarnation, "Hello", TimeSpan.FromSeconds(10));

            firstSystem.Terminate();
            secondSystem.Terminate();
        }

        private class Relaying : ReceiveActor
        {
            private IActorRef _relay;

            public Relaying(IActorRef relay)
            {
                _relay = relay;

                ReceiveAny(msg => _relay.Tell(msg));
            }
        }
    }
}