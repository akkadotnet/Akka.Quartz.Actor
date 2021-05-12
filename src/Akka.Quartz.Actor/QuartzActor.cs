using System;
using System.Collections.Specialized;
using Akka.Actor;
using Akka.Dispatch;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Akka.Quartz.Actor.Exceptions;
using Quartz;
using Quartz.Impl;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor
{
    /// <summary>
    /// The base quartz scheduling actor. Handles a single quartz scheduler
    /// and processes Add and Remove messages.
    /// </summary>
    public class QuartzActor : ActorBase
    {
        protected readonly IScheduler _scheduler;

        private readonly bool _externallySupplied;

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
            if (String.IsNullOrWhiteSpace(props.Get(StdSchedulerFactory.PropertySchedulerInstanceName)))
            {
                props.Set(StdSchedulerFactory.PropertySchedulerInstanceName, Guid.NewGuid().ToString());
            }

            ActorTaskScheduler.RunTask(async () =>
            {
                if (props == null)
                    Scheduler = await new StdSchedulerFactory().GetScheduler();
                else
                    Scheduler = await new StdSchedulerFactory(props).GetScheduler();

                await Scheduler.Start();
                OnSchedulerCreated(Scheduler);
            });
        }

        protected virtual void OnSchedulerCreated(IScheduler scheduler) { }

        public QuartzActor(IScheduler scheduler)
        {
            Scheduler = scheduler;
            _externallySupplied = true;
            OnSchedulerCreated(Scheduler);
        }

        protected override bool Receive(object message)
        {
            return message.Match().With<CreateJob>(CreateJobCommand).With<RemoveJob>(RemoveJobCommand).WasHandled;
        }

        protected override void PostStop()
        {
            if (!_externallySupplied)
            {
                ActorTaskScheduler.RunTask(() => Scheduler.Shutdown());
            }
            base.PostStop();
        }

        protected virtual void CreateJobCommand(CreateJob createJob)
        {
            ActorTaskScheduler.RunTask(async () =>
            {
                if (createJob.To == null)
                {
                    Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException(nameof(createJob.To))));
                }
                if (createJob.Trigger == null)
                {
                    Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException(nameof(createJob.Trigger))));
                }
                else
                {
                    try
                    {
                        var job =
                        QuartzJob.CreateBuilderWithData(createJob.To, createJob.Message)
                            .WithIdentity(createJob.Trigger.JobKey)
                            .Build();
                        await Scheduler.ScheduleJob(job, createJob.Trigger);
                        Context.Sender.Tell(new JobCreated(createJob.Trigger.JobKey, createJob.Trigger.Key));
                    }
                    catch (Exception ex)
                    {
                        Context.Sender.Tell(new CreateJobFail(createJob.Trigger.JobKey, createJob.Trigger.Key, ex));
                    }
                }
            });
        }

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