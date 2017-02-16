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
    public class QuartzActorSpec : TestKit.Xunit2.TestKit
    {
        [Fact]
        public void QuartzActor_Should_Create_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello", TriggerBuilder.Create().WithCronSchedule("*0/10 * * * * ?").Build()));
            ExpectMsg<JobCreated>();
            probe.ExpectMsg("Hello", TimeSpan.FromSeconds(11));
            Thread.Sleep(TimeSpan.FromSeconds(10));
            probe.ExpectMsg("Hello");
            Sys.Stop(quartzActor);            
        }

        [Fact]
        public void QuartzActor_Should_Remove_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello remove", TriggerBuilder.Create().WithCronSchedule("0/10 * * * * ?").Build()));
            var jobCreated = ExpectMsg<JobCreated>();
            probe.ExpectMsg("Hello remove", TimeSpan.FromSeconds(11));
            quartzActor.Tell(new RemoveJob(jobCreated.JobKey, jobCreated.TriggerKey));
            ExpectMsg<JobRemoved>();
            Thread.Sleep(TimeSpan.FromSeconds(10));
            probe.ExpectNoMsg(TimeSpan.FromSeconds(10));
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Fail_With_Null_Trigger()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello", null));
            var failedJob = ExpectMsg<CreateJobFail>();
            Assert.NotNull(failedJob.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Fail_With_Null_Actor()
        {
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(null, "Hello", TriggerBuilder.Create().WithCronSchedule(" * * * * * ?").Build()));
            var failedJob = ExpectMsg<CreateJobFail>();
            Assert.NotNull(failedJob.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Not_Remove_UnExisting_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new RemoveJob(new JobKey("key"), new TriggerKey("key")));
            var failure=ExpectMsg<RemoveJobFail>();
            Assert.IsType<JobNotFoundException>(failure.Reason);
            Sys.Stop(quartzActor);
        }
    }
}