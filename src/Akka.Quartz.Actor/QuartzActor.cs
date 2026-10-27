using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Dispatch;
using Akka.Event;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Akka.Quartz.Actor.Exceptions;
using Quartz;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor
{
    /// <summary>
    /// The base quartz scheduling actor. Handles a single quartz scheduler
    /// and processes Add and Remove messages.
    /// </summary>
    public class QuartzActor : ActorBase
    {
        /// <summary>
        /// Quartz no longer exposes this as a public constant (StdSchedulerFactory was removed in 4.0),
        /// so it's kept here for callers that used to reach it via StdSchedulerFactory.PropertySchedulerInstanceName.
        /// </summary>
        public const string PropertySchedulerInstanceName = "quartz.scheduler.instanceName";

        protected IScheduler Scheduler { get; private set; }

        private readonly bool _externallySupplied;
        private OwnedSchedulerShutdown.OwnedScheduler _ownedScheduler;

        public QuartzActor()
        {
            Init(null);
        }

        public QuartzActor(NameValueCollection props)
        {
            Init(props);
        }

        private void Init(NameValueCollection props)
        {
            if (props == null)
            {
                props = new NameValueCollection();
            }
            if (String.IsNullOrWhiteSpace(props.Get(PropertySchedulerInstanceName)))
            {
                props.Set(PropertySchedulerInstanceName, Guid.NewGuid().ToString());
            }

            _ownedScheduler = new OwnedSchedulerShutdownExtension().Apply(Context.System).Register(props);
            ActorTaskScheduler.RunTask(async () =>
            {
                Scheduler = await _ownedScheduler.Scheduler;
                PrepareScheduler(Scheduler);
                await _ownedScheduler.StartAsync(Scheduler);
                OnSchedulerCreated(Scheduler);
            });
        }

        protected virtual void OnSchedulerCreated(IScheduler scheduler) { }

        internal virtual void PrepareScheduler(IScheduler scheduler) { }

        public QuartzActor(IScheduler scheduler)
        {
            Scheduler = scheduler;
            _externallySupplied = true;
            PrepareScheduler(Scheduler);
            OnSchedulerCreated(Scheduler);
        }

        protected override bool Receive(object message)
        {
            switch (message)
            {
                case CreateJob createJob:
                    CreateJobCommand(createJob);
                    return true;
                case RemoveJob removeJob: 
                    RemoveJobCommand(removeJob); 
                    return true;
                default:
                    return false;
            }
        }

        protected override void PostStop()
        {
            if (!_externallySupplied)
            {
                // PostStop cannot resume work through an actor mailbox that is being terminated.
                _ = ShutdownSchedulerAsync(_ownedScheduler, Context.GetLogger());
            }
            base.PostStop();
        }

        private static async Task ShutdownSchedulerAsync(OwnedSchedulerShutdown.OwnedScheduler owned, ILoggingAdapter log)
        {
            try
            {
                if (owned != null) await owned.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                log.Error(exception, "Failed to dispose the actor-owned Quartz scheduler.");
            }
        }

        protected virtual void CreateJobCommand(CreateJob createJob)
        {
            ActorTaskScheduler.RunTask(async () =>
            {
                if (createJob.To == null)
                {
                    Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException(nameof(createJob.To))));
                    return;
                }
                if (createJob.Trigger == null)
                {
                    Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException(nameof(createJob.Trigger))));
                    return;
                }
                else
                {
                    try
                    {
                        var job =
                        QuartzJob.CreateBuilderWithData(createJob.To, createJob.Message)
                            .WithIdentity(ResolveJobKey(createJob.Trigger))
                            .Build();
                        await Scheduler.ScheduleJob(job, createJob.Trigger, createJob.Options);
                        Context.Sender.Tell(new JobCreated(job.Key, createJob.Trigger.Key));
                    }
                    catch (Exception ex)
                    {
                        Context.Sender.Tell(new CreateJobFail(createJob.Trigger.JobKey, createJob.Trigger.Key, ex));
                    }
                }
            });
        }

        /// <summary>
        /// The job key to schedule a trigger under.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An explicitly named job is always preserved. When the trigger names no job,
        /// <see cref="JobBuilder.Build"/> would otherwise mint a fresh random <see cref="JobKey"/> on
        /// every call, so each create command would target a different job. Quartz replaces a trigger
        /// by key and removes the old trigger without deleting the job it pointed at, so a replacement
        /// would leave the original job behind with no trigger.
        /// </para>
        /// <para>
        /// Deriving the key from the trigger identity keeps the mapping stable across commands, which
        /// is what makes <see cref="ScheduleJobOptions.Replacing"/> replace the job rather than orphan
        /// it. Both the trigger name and the trigger group participate, so distinct trigger identities
        /// produce distinct job identities.
        /// </para>
        /// <para>
        /// The derived key reuses the trigger's own name and group, so it can coincide with a job that
        /// was named explicitly under the same name and group. That collision is deliberate: it keeps
        /// the mapping predictable, and a caller who names a job explicitly and also schedules a
        /// trigger of the same identity is already addressing one job. Callers who need to keep the two
        /// apart should give the trigger an explicit <c>ForJob</c> identity.
        /// </para>
        /// <para>
        /// This does not clean up jobs that earlier versions already orphaned in a persistent store.
        /// Those records have random keys and no trigger, so they cannot be attributed to a caller
        /// safely; removing them automatically would risk deleting a job someone else still owns.
        /// </para>
        /// </remarks>
        protected static JobKey ResolveJobKey(ITrigger trigger) =>
            trigger.JobKey ?? new JobKey(trigger.Key.Name, trigger.Key.Group);

        protected virtual void RemoveJobCommand(RemoveJob removeJob)
        {
            var sender = Context.Sender;
            ActorTaskScheduler.RunTask(async () =>
            {
                try
                {
                    var deleted = await Scheduler.DeleteJob(removeJob.JobKey);
                    if (deleted)
                    {
                        sender.Tell(new JobRemoved(removeJob.JobKey, removeJob.TriggerKey));
                    }
                    else
                    {
                        sender.Tell(new RemoveJobFail(removeJob.JobKey, removeJob.TriggerKey, new JobNotFoundException()));
                    }
                }
                catch (Exception ex)
                {
                    sender.Tell(new RemoveJobFail(removeJob.JobKey, removeJob.TriggerKey, ex));
                }
            });
        }
    }
}
