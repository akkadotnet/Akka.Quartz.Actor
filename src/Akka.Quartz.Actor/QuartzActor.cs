using System;
using System.Collections.Specialized;
using Akka.Actor;
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
            _scheduler = new StdSchedulerFactory().GetScheduler();
        }

        public QuartzActor(NameValueCollection props)
        {
            _scheduler = new StdSchedulerFactory(props).GetScheduler();
        }

        public QuartzActor(IScheduler scheduler)
        {
            _scheduler = scheduler;
            _externallySupplied = true;
        }

        protected override bool Receive(object message)
        {
            return message.Match().With<CreateJob>(CreateJobCommand).With<RemoveJob>(RemoveJobCommand).WasHandled;
        }

        protected override void PreStart()
        {
            if (!_externallySupplied)
            {
                _scheduler.Start();    
            }
            base.PreStart();
        }

        protected override void PostStop()
        {
            if (!_externallySupplied)
            {
                _scheduler.Shutdown();
            }
            base.PostStop();
        }

        protected virtual void CreateJobCommand(CreateJob createJob)
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
                    _scheduler.ScheduleJob(job, createJob.Trigger);

                    Context.Sender.Tell(new JobCreated(createJob.Trigger.JobKey, createJob.Trigger.Key));
                }
                catch (Exception ex)
                {
                    Context.Sender.Tell(new CreateJobFail(createJob.Trigger.JobKey, createJob.Trigger.Key, ex));
                }
            }
        }

        protected virtual void RemoveJobCommand(RemoveJob removeJob)
        {
            try
            {
                var deleted = _scheduler.DeleteJob(removeJob.JobKey);
                if (deleted)
                {
                    Context.Sender.Tell(new JobRemoved(removeJob.JobKey, removeJob.TriggerKey));
                }
                else
                {
                    Context.Sender.Tell(new RemoveJobFail(removeJob.JobKey, removeJob.TriggerKey, new JobNotFoundException()));
                }
            }
            catch (Exception ex)
            {
                Context.Sender.Tell(new RemoveJobFail(removeJob.JobKey, removeJob.TriggerKey, ex));
            }
        }
    }
}