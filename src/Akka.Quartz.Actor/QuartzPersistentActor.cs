using System;
using System.Collections.Specialized;
using Akka.Actor;
using Akka.Dispatch;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor
{
    /// <summary>
    /// The persistent quartz scheduling actor. Handles a single quartz scheduler
    /// and processes CreatePersistentJob and RemoveJob messages.
    /// </summary>
    public class QuartzPersistentActor : QuartzActor
    {
        /// <summary>
        /// Creates an actor-owned scheduler with Quartz's default in-memory job store; nothing it schedules
        /// survives a process restart.
        /// </summary>
        /// <remarks>
        /// Quartz 3 returned an existing scheduler registered under the same name, so this constructor could
        /// attach to a persistent scheduler configured elsewhere in the process. Quartz 4 removed that registry.
        /// </remarks>
        [Obsolete("Quartz 4 no longer looks schedulers up by name: this always creates a new in-memory (RAMJobStore) scheduler. " +
                  "Pass the job store configuration to QuartzPersistentActor(NameValueCollection), or supply a scheduler with QuartzPersistentActor(IScheduler).")]
        public QuartzPersistentActor(string schedulerName)
            : this(new NameValueCollection() { [PropertySchedulerInstanceName] = schedulerName })
        {
        }

        /// <summary>
        /// Creates and owns a scheduler from Quartz properties, including the job store configuration
        /// required for jobs to survive a restart.
        /// </summary>
        public QuartzPersistentActor(NameValueCollection props)
            : base(props)
        {
        }

        public QuartzPersistentActor(IScheduler scheduler)
            : base(scheduler)
        { }

        internal override void PrepareScheduler(IScheduler scheduler) => InstallActorSystem(scheduler);

        protected override void OnSchedulerCreated(IScheduler scheduler) => InstallActorSystem(scheduler);

        private void InstallActorSystem(IScheduler scheduler)
        {
            // The post-start compatibility callback must not briefly remove context used by recovered jobs.
            if (scheduler.Context.TryGetValue(QuartzPersistentJob.SysKey, out var existing)
                && ReferenceEquals(existing, Context.System)) return;
            scheduler.Context[QuartzPersistentJob.SysKey] = Context.System;
        }

        protected override bool Receive(object message)
        {
            switch (message)
            {
                case CreatePersistentJob createPersistentJob:
                    CreateJobCommand(createPersistentJob);
                    return true;
                case RemoveJob removeJob:
                    RemoveJobCommand(removeJob);
                    return true;
                default:
                    return false;
            }
        }

        private void CreateJobCommand(CreatePersistentJob createJob)
        {
            if (createJob.To == null)
            {
                Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException("createJob.To")));
                return;
            }
            if (createJob.Trigger == null)
            {
                Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException("createJob.Trigger")));
                return;
            }
            else
            {
                ActorTaskScheduler.RunTask(async () =>
                {
                    try
                    {
                        var job =
                        QuartzPersistentJob.CreateBuilderWithData(createJob.To, createJob.Message, Context.System)
                            .WithIdentity(createJob.Trigger.JobKey)
                            .Build();
                        await Scheduler.ScheduleJob(job, createJob.Trigger, createJob.Options);

                        Context.Sender.Tell(new JobCreated(createJob.Trigger.JobKey, createJob.Trigger.Key));
                    }
                    catch (Exception ex)
                    {
                        Context.Sender.Tell(new CreateJobFail(createJob.Trigger.JobKey, createJob.Trigger.Key, ex));
                    }
                });
            }
        }
    }
}
