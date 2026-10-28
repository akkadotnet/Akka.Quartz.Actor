using System;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Akka.Quartz.Actor.Exceptions;
using Quartz;
using Xunit;

namespace Akka.Quartz.Actor.Tests
{
    public class QuartzActorSpec : TestKit.Xunit.TestKit
    {
        [Fact]
        public void QuartzActor_Should_Create_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello", TriggerBuilder.Create().WithCronSchedule("0/10 * * * * ?").Build()));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectMsg("Hello", TimeSpan.FromSeconds(11), cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectMsg("Hello", TimeSpan.FromSeconds(11), cancellationToken: TestContext.Current.CancellationToken);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Remove_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello remove", TriggerBuilder.Create().WithCronSchedule("0/10 * * * * ?").Build()));
            var jobCreated = ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectMsg("Hello remove", TimeSpan.FromSeconds(11), cancellationToken: TestContext.Current.CancellationToken);
            quartzActor.Tell(new RemoveJob(jobCreated.JobKey, jobCreated.TriggerKey));
            ExpectMsg<JobRemoved>(cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectNoMsg(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Replace_Job()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello old", TriggerBuilder.Create().WithIdentity("greeting").WithCronSchedule("0/3 * * * * ?").Build()));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectMsg("Hello old", TimeSpan.FromSeconds(4), cancellationToken: TestContext.Current.CancellationToken);
            
            quartzActor.Tell(new CreateJob(probe, "Hello new", TriggerBuilder.Create().WithIdentity("greeting").WithCronSchedule("0/6 * * * * ?").StartAt(DateTimeOffset.UtcNow).Build(), ScheduleJobOptions.Replacing));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectMsg("Hello new", TimeSpan.FromSeconds(7), cancellationToken: TestContext.Current.CancellationToken);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Fail_With_Null_Trigger()
        {
            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(probe, "Hello", null));
            var failedJob = ExpectMsg<CreateJobFail>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(failedJob.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Fail_With_Null_Actor()
        {
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new CreateJob(null, "Hello", TriggerBuilder.Create().WithCronSchedule(" * * * * * ?").Build()));
            var failedJob = ExpectMsg<CreateJobFail>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(failedJob.Reason);
            Sys.Stop(quartzActor);
        }

        [Fact]
        public void QuartzActor_Should_Not_Remove_UnExisting_Job()
        {
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzActor()), "QuartzActor");
            quartzActor.Tell(new RemoveJob(new JobKey("key"), new TriggerKey("key")));
            var failure = ExpectMsg<RemoveJobFail>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsType<JobNotFoundException>(failure.Reason);
            Sys.Stop(quartzActor);
        }
    }
}