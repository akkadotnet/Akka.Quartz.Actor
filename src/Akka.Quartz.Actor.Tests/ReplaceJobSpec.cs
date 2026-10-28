using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Xunit;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor.Tests
{
    /// <summary>
    /// Replacement semantics for <see cref="ScheduleJobOptions.Replacing"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests inspect the scheduler's own job and trigger state instead of only waiting for a
    /// message. Receiving the replacement payload proves the new trigger fires; it does not prove the
    /// original job record was replaced. Quartz replaces a trigger by key and, in
    /// <c>RAMJobStore.AddTriggerNoLock</c>, removes the old trigger with
    /// <c>removeOrphanedJob: false</c> - so a replacement that lands on a different job key leaves the
    /// original job behind with no trigger pointing at it.
    /// </para>
    /// <para>
    /// Every trigger is built fresh for each command. Quartz assigns the scheduled job key to the
    /// supplied trigger when it has none (<c>QuartzScheduler.ScheduleJob</c>), so reusing one trigger
    /// instance across a create and a replace would hide the defect by carrying the first job key into
    /// the second command.
    /// </para>
    /// <para>
    /// The original schedule is dormant (<c>0 0 0 1 1 ?</c>, once a year) so only the replacement can
    /// deliver a message, and the replacement schedule fires every second. That keeps the tests fast
    /// and removes the wall-clock dependence of waiting on several real cron firings.
    /// </para>
    /// </remarks>
    public class ReplaceJobSpec : TestKit.Xunit.TestKit
    {
        private const string DormantCron = "0 0 0 1 1 ?";
        private const string FastCron = "* * * * * ?";
        private const string MessageKey = "message";

        private static readonly JobKey Job = new JobKey("greeting-job");
        private static readonly TriggerKey TriggerId = new TriggerKey("greeting-trigger");
        private static readonly TriggerKey SecondTriggerId = new TriggerKey("second-trigger");

        /// <summary>A trigger that names both the job and the trigger.</summary>
        private static ITrigger Trigger(string cron) => TriggerBuilder.Create()
            .WithIdentity(TriggerId)
            .ForJob(Job)
            .WithCronSchedule(cron)
            .Build();

        /// <summary>A second trigger for the same job, under a different trigger key.</summary>
        private static ITrigger SecondTrigger(string cron) => TriggerBuilder.Create()
            .WithIdentity(SecondTriggerId)
            .ForJob(Job)
            .WithCronSchedule(cron)
            .Build();

        /// <summary>
        /// A trigger that names only itself. <see cref="TriggerBuilder.Build"/> leaves
        /// <see cref="ITrigger.JobKey"/> null in this case, and <see cref="JobBuilder.Build"/>
        /// substitutes a fresh random <see cref="JobKey"/> for every call.
        /// </summary>
        private static ITrigger TriggerWithoutJobKey(string cron) => TriggerBuilder.Create()
            .WithIdentity(TriggerId)
            .WithCronSchedule(cron)
            .Build();

        /// <summary>A trigger with an explicit name and group, and no job identity.</summary>
        private static ITrigger TriggerWithoutJobKey(string cron, string name, string group) => TriggerBuilder.Create()
            .WithIdentity(name, group)
            .WithCronSchedule(cron)
            .Build();

        private static async Task<List<JobHeader>> AllJobs(IScheduler scheduler)
        {
            var page = await scheduler.QueryJobs(new JobQuery { Take = PagedQuery.All });
            return page.Items.ToList();
        }

        private static async Task<List<TriggerHeader>> TriggersFor(IScheduler scheduler, JobKey jobKey)
        {
            var page = await scheduler.QueryTriggers(new TriggerQuery { Job = jobKey, Take = PagedQuery.All });
            return page.Items.ToList();
        }

        /// <summary>
        /// Reads the stored payload. <see cref="QuartzJob"/> stores the message as-is;
        /// <see cref="QuartzPersistentJob"/> stores it Base64-encoded so it survives a persistent store.
        /// </summary>
        private static string StoredMessage(IJobDetail detail)
        {
            var stored = detail.JobDataMap[MessageKey];
            return stored is string text ? text : stored?.ToString();
        }

        private static string StoredPersistentMessage(IJobDetail detail)
        {
            var stored = detail.JobDataMap[MessageKey];
            if (stored is not string text) return stored?.ToString();

            // Akka's serializer round-trips a string as a quoted JSON string, so the decoded payload
            // arrives as "new" rather than new.
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text));
            return decoded.Length >= 2 && decoded[0] == '"' && decoded[^1] == '"'
                ? decoded[1..^1]
                : decoded;
        }

        // ---------------------------------------------------------------- QuartzActor

        [Fact]
        public async Task QuartzActor_Should_Replace_Job_Under_Stable_Identities()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreateJob(probe, "old", Trigger(DormantCron)));
            var created = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(Job, created.JobKey);
            Assert.Equal(TriggerId, created.TriggerKey);

            // The same keys without Replacing must be rejected, not silently accepted.
            quartzActor.Tell(new CreateJob(probe, "old", Trigger(DormantCron)));
            var failure = ExpectMsg<CreateJobFail>(cancellationToken: cancellationToken);
            Assert.IsType<ObjectAlreadyExistsException>(failure.Reason);

            // The same keys with Replacing must be accepted and must swap payload and schedule.
            quartzActor.Tell(new CreateJob(probe, "new", Trigger(FastCron), ScheduleJobOptions.Replacing));
            var replaced = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(Job, replaced.JobKey);
            Assert.Equal(TriggerId, replaced.TriggerKey);
            probe.ExpectMsg("new", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

            // The original schedule is dormant, so it cannot fire in this window regardless of whether
            // replacement worked. This drain is a bounded observation, not proof that old deliveries
            // stopped: Quartz clones the job detail into a firing bundle, so an already-dispatched
            // firing can still execute after replacement. The scheduler-state assertions below are the
            // actual proof that the job was replaced.
            var delivered = probe.ReceiveWhile<string>(
                TimeSpan.FromSeconds(2),
                msg => msg as string,
                100,
                cancellationToken);
            Assert.DoesNotContain("old", delivered);

            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(Job, jobs[0].Key);

            var triggers = await TriggersFor(scheduler, Job);
            Assert.Single(triggers);
            Assert.Equal(TriggerId, triggers[0].Key);
            Assert.Equal(FastCron, Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(TriggerId, cancellationToken)).CronExpressionString);

            var detail = await scheduler.GetJobDetail(Job, cancellationToken);
            Assert.NotNull(detail);
            Assert.Equal("new", StoredMessage(detail!));

            // The reported key must resolve to the job that was actually scheduled.
            Assert.NotNull(await scheduler.GetJobDetail(replaced.JobKey, cancellationToken));

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzActor_Should_Preserve_An_Explicit_Job_Identity()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreateJob(probe, "old", Trigger(DormantCron)));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // An explicitly named job must never be renamed by the actor.
            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(Job, jobs[0].Key);

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzActor_Should_Not_Orphan_The_Original_Job_When_Replacing_By_Trigger_Identity()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreateJob(probe, "old", TriggerWithoutJobKey(DormantCron)));
            var created = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // The derived key must be the trigger's own name and group.
            var expectedKey = new JobKey(TriggerId.Name, TriggerId.Group);
            Assert.Equal(expectedKey, created.JobKey);

            quartzActor.Tell(new CreateJob(probe, "new", TriggerWithoutJobKey(FastCron), ScheduleJobOptions.Replacing));
            var replaced = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            probe.ExpectMsg("new", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

            // The replacement must reuse the same job identity, not delete and recreate under a new key.
            Assert.Equal(expectedKey, replaced.JobKey);

            // Replacing the trigger must not leave the original job behind without a trigger.
            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(expectedKey, jobs[0].Key);

            var triggers = await TriggersFor(scheduler, jobs[0].Key);
            Assert.Single(triggers);
            Assert.Equal(TriggerId, triggers[0].Key);

            var detail = await scheduler.GetJobDetail(jobs[0].Key, cancellationToken);
            Assert.NotNull(detail);
            Assert.Equal("new", StoredMessage(detail!));

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzActor_Should_Not_Remove_Other_Triggers_Of_The_Same_Job()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreateJob(probe, "first", Trigger(DormantCron)));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // A second trigger for the same job. Replacing is required because the job key already exists.
            quartzActor.Tell(new CreateJob(probe, "second", SecondTrigger(DormantCron), ScheduleJobOptions.Replacing));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(2, (await TriggersFor(scheduler, Job)).Count);

            // Record the second trigger's schedule and state before the replacement.
            var secondBefore = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(SecondTriggerId, cancellationToken));
            var secondStateBefore = await scheduler.GetTriggerState(SecondTriggerId, cancellationToken);

            // Replacing one trigger must leave the other one alone.
            quartzActor.Tell(new CreateJob(probe, "first-replaced", Trigger(FastCron), ScheduleJobOptions.Replacing));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            probe.ExpectMsg("first-replaced", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

            var triggers = await TriggersFor(scheduler, Job);
            Assert.Equal(2, triggers.Count);
            Assert.Contains(triggers, t => t.Key.Equals(TriggerId));
            Assert.Contains(triggers, t => t.Key.Equals(SecondTriggerId));

            // The surviving trigger must keep its schedule and state, not merely its key.
            var secondAfter = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(SecondTriggerId, cancellationToken));
            Assert.Equal(secondBefore.CronExpressionString, secondAfter.CronExpressionString);
            Assert.Equal(secondStateBefore, await scheduler.GetTriggerState(SecondTriggerId, cancellationToken));

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzActor_Should_Derive_Distinct_Job_Keys_From_Trigger_Identity()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            // Same name, different group.
            quartzActor.Tell(new CreateJob(probe, "a", TriggerWithoutJobKey(DormantCron, "same-name", "group-one")));
            var first = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // Same name, different group again.
            quartzActor.Tell(new CreateJob(probe, "b", TriggerWithoutJobKey(DormantCron, "same-name", "group-two")));
            var second = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // Different name, same group as the first.
            quartzActor.Tell(new CreateJob(probe, "c", TriggerWithoutJobKey(DormantCron, "other-name", "group-one")));
            var third = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // Both the trigger name and the trigger group must participate, so all three are distinct.
            Assert.Equal(new JobKey("same-name", "group-one"), first.JobKey);
            Assert.Equal(new JobKey("same-name", "group-two"), second.JobKey);
            Assert.Equal(new JobKey("other-name", "group-one"), third.JobKey);

            var jobs = await AllJobs(scheduler);
            Assert.Equal(3, jobs.Count);
            Assert.Equal(3, jobs.Select(j => j.Key).Distinct().Count());

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzActor_Should_Not_Overwrite_An_Explicitly_Named_Job_When_Deriving()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            // A job named explicitly, under the same name and group a derived key would use.
            quartzActor.Tell(new CreateJob(probe, "explicit", Trigger(DormantCron)));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            // A trigger with that same identity and no ForJob derives the same job key, so without
            // Replacing it must be rejected rather than silently overwriting the explicit job.
            quartzActor.Tell(new CreateJob(probe, "derived", TriggerWithoutJobKey(DormantCron, "greeting-job", JobKey.DefaultGroup)));
            var failure = ExpectMsg<CreateJobFail>(cancellationToken: cancellationToken);
            Assert.IsType<ObjectAlreadyExistsException>(failure.Reason);

            // The explicit job is untouched.
            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(Job, jobs[0].Key);

            var detail = await scheduler.GetJobDetail(Job, cancellationToken);
            Assert.NotNull(detail);
            Assert.Equal("explicit", StoredMessage(detail!));

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzActor_Should_Overwrite_An_Explicitly_Named_Job_When_Deriving_With_Replacing()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var explicitProbe = CreateTestProbe(Sys);
            var derivedProbe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(Props.Create(() => new CapturingQuartzActor()), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            // An explicit job under the name and group a derived key would use, with its own trigger.
            quartzActor.Tell(new CreateJob(explicitProbe, "explicit", Trigger(DormantCron)));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            var explicitTriggerBefore = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(TriggerId, cancellationToken));
            var explicitStateBefore = await scheduler.GetTriggerState(TriggerId, cancellationToken);

            // A trigger with that same identity and no ForJob derives the same job key. With Replacing
            // it overwrites the explicit job - including its recipient and payload. The explicit job's
            // own trigger, under a different trigger key, survives and now executes the replacement.
            //
            // This is the documented consequence of the identity convention: job and trigger identities
            // are separate namespaces, and the derived mapping deliberately makes them address one job.
            quartzActor.Tell(new CreateJob(
                derivedProbe, "derived", TriggerWithoutJobKey(FastCron, "greeting-job", JobKey.DefaultGroup), ScheduleJobOptions.Replacing));
            var replaced = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(Job, replaced.JobKey);

            // The replacement payload goes to the derived recipient, not the original one.
            derivedProbe.ExpectMsg("derived", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
            explicitProbe.ExpectNoMsg(TimeSpan.FromSeconds(2), cancellationToken);

            // One job, now carrying the derived payload and the derived recipient.
            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(Job, jobs[0].Key);

            var detail = await scheduler.GetJobDetail(Job, cancellationToken);
            Assert.NotNull(detail);
            Assert.Equal("derived", StoredMessage(detail!));
            Assert.Equal(derivedProbe.Ref, detail!.JobDataMap["actor"]);

            // Both triggers remain: the explicit job's trigger and the derived one.
            var triggers = await TriggersFor(scheduler, Job);
            Assert.Equal(2, triggers.Count);
            Assert.Contains(triggers, t => t.Key.Equals(TriggerId));
            Assert.Contains(triggers, t => t.Key.Equals(new TriggerKey("greeting-job", JobKey.DefaultGroup)));

            // The surviving explicit trigger keeps its schedule and state.
            var explicitTriggerAfter = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(TriggerId, cancellationToken));
            Assert.Equal(explicitTriggerBefore.CronExpressionString, explicitTriggerAfter.CronExpressionString);
            Assert.Equal(explicitStateBefore, await scheduler.GetTriggerState(TriggerId, cancellationToken));

            Sys.Stop(quartzActor);
        }

        // ------------------------------------------------------ QuartzPersistentActor

        [Fact]
        public async Task QuartzPersistentActor_Should_Replace_Job_Under_Stable_Identities()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(
                Props.Create(() => new CapturingQuartzPersistentActor(Named(Guid.NewGuid().ToString()))), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "old", Trigger(DormantCron)));
            var created = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(Job, created.JobKey);
            Assert.Equal(TriggerId, created.TriggerKey);

            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "old", Trigger(DormantCron)));
            var failure = ExpectMsg<CreateJobFail>(cancellationToken: cancellationToken);
            Assert.IsType<ObjectAlreadyExistsException>(failure.Reason);

            quartzActor.Tell(new CreatePersistentJob(
                probe.Ref.Path, "new", Trigger(FastCron), ScheduleJobOptions.Replacing));
            var replaced = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(Job, replaced.JobKey);
            Assert.Equal(TriggerId, replaced.TriggerKey);
            probe.ExpectMsg("new", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

            // The original schedule is dormant, so it cannot fire in this window regardless of whether
            // replacement worked. This drain is a bounded observation, not proof that old deliveries
            // stopped: Quartz clones the job detail into a firing bundle, so an already-dispatched
            // firing can still execute after replacement. The scheduler-state assertions below are the
            // actual proof that the job was replaced.
            var delivered = probe.ReceiveWhile<string>(
                TimeSpan.FromSeconds(2),
                msg => msg as string,
                100,
                cancellationToken);
            Assert.DoesNotContain("old", delivered);

            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(Job, jobs[0].Key);

            var triggers = await TriggersFor(scheduler, Job);
            Assert.Single(triggers);
            Assert.Equal(TriggerId, triggers[0].Key);
            Assert.Equal(FastCron, Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(TriggerId, cancellationToken)).CronExpressionString);

            var detail = await scheduler.GetJobDetail(Job, cancellationToken);
            Assert.NotNull(detail);
            Assert.Equal("new", StoredPersistentMessage(detail!));

            Assert.NotNull(await scheduler.GetJobDetail(replaced.JobKey, cancellationToken));

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzPersistentActor_Should_Not_Orphan_The_Original_Job_When_Replacing_By_Trigger_Identity()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(
                Props.Create(() => new CapturingQuartzPersistentActor(Named(Guid.NewGuid().ToString()))), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "old", TriggerWithoutJobKey(DormantCron)));
            var created = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            var expectedKey = new JobKey(TriggerId.Name, TriggerId.Group);
            Assert.Equal(expectedKey, created.JobKey);

            quartzActor.Tell(new CreatePersistentJob(
                probe.Ref.Path, "new", TriggerWithoutJobKey(FastCron), ScheduleJobOptions.Replacing));
            var replaced = ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            probe.ExpectMsg("new", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

            Assert.Equal(expectedKey, replaced.JobKey);

            var jobs = await AllJobs(scheduler);
            Assert.Single(jobs);
            Assert.Equal(expectedKey, jobs[0].Key);

            var triggers = await TriggersFor(scheduler, jobs[0].Key);
            Assert.Single(triggers);
            Assert.Equal(TriggerId, triggers[0].Key);

            var detail = await scheduler.GetJobDetail(jobs[0].Key, cancellationToken);
            Assert.NotNull(detail);
            Assert.Equal("new", StoredPersistentMessage(detail!));

            Sys.Stop(quartzActor);
        }

        [Fact]
        public async Task QuartzPersistentActor_Should_Not_Remove_Other_Triggers_Of_The_Same_Job()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var probe = CreateTestProbe(Sys);
            Sys.EventStream.Subscribe(TestActor, typeof(SchedulerCaptured));
            var quartzActor = Sys.ActorOf(
                Props.Create(() => new CapturingQuartzPersistentActor(Named(Guid.NewGuid().ToString()))), "QuartzActor");
            var scheduler = ExpectMsg<SchedulerCaptured>(cancellationToken: cancellationToken).Scheduler;

            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "first", Trigger(DormantCron)));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);

            quartzActor.Tell(new CreatePersistentJob(
                probe.Ref.Path, "second", SecondTrigger(DormantCron), ScheduleJobOptions.Replacing));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            Assert.Equal(2, (await TriggersFor(scheduler, Job)).Count);

            var secondBefore = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(SecondTriggerId, cancellationToken));
            var secondStateBefore = await scheduler.GetTriggerState(SecondTriggerId, cancellationToken);

            quartzActor.Tell(new CreatePersistentJob(
                probe.Ref.Path, "first-replaced", Trigger(FastCron), ScheduleJobOptions.Replacing));
            ExpectMsg<JobCreated>(cancellationToken: cancellationToken);
            probe.ExpectMsg("first-replaced", TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

            var triggers = await TriggersFor(scheduler, Job);
            Assert.Equal(2, triggers.Count);
            Assert.Contains(triggers, t => t.Key.Equals(TriggerId));
            Assert.Contains(triggers, t => t.Key.Equals(SecondTriggerId));

            var secondAfter = Assert.IsAssignableFrom<ICronTrigger>(await scheduler.GetTrigger(SecondTriggerId, cancellationToken));
            Assert.Equal(secondBefore.CronExpressionString, secondAfter.CronExpressionString);
            Assert.Equal(secondStateBefore, await scheduler.GetTriggerState(SecondTriggerId, cancellationToken));

            Sys.Stop(quartzActor);
        }

        // ------------------------------------------------------------------- helpers

        private static NameValueCollection Named(string schedulerName) =>
            new NameValueCollection { [QuartzActor.PropertySchedulerInstanceName] = schedulerName };

        private sealed record SchedulerCaptured(IScheduler Scheduler);

        private sealed class CapturingQuartzActor : QuartzActor
        {
            protected override void OnSchedulerCreated(IScheduler scheduler) =>
                Context.System.EventStream.Publish(new SchedulerCaptured(scheduler));
        }

        private sealed class CapturingQuartzPersistentActor : QuartzPersistentActor
        {
            public CapturingQuartzPersistentActor(NameValueCollection props) : base(props) { }

            protected override void OnSchedulerCreated(IScheduler scheduler)
            {
                base.OnSchedulerCreated(scheduler);
                Context.System.EventStream.Publish(new SchedulerCaptured(scheduler));
            }
        }
    }
}
