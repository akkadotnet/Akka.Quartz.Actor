using System;
using System.Collections.Specialized;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz.Impl;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor
{
    /// <summary>
    /// The persistent quartz scheduling actor. Handles a single quartz scheduler
    /// and processes CreatePersistentJob and RemoveJob messages.
    /// </summary>
    public class QuartzPersistentActor : QuartzActor
    {

        public QuartzPersistentActor()
        {
            this.AddSystemToScheduler();
        }

        public QuartzPersistentActor(IScheduler scheduler)
            : base(scheduler)
        { }

        protected override void OnSchedulerCreated(IScheduler scheduler)
        {
            if (!scheduler.Context.ContainsKey(QuartzPersistentJob.SysKey))
            {
                scheduler.Context.Add(QuartzPersistentJob.SysKey, Context.System);
            }
            else
            {
                scheduler.Context.Remove(QuartzPersistentJob.SysKey);
                scheduler.Context.Add(QuartzPersistentJob.SysKey, Context.System);
            }
        }

        protected override bool Receive(object message)
        {
            return message.Match().With<CreatePersistentJob>(CreateJobCommand).With<RemoveJob>(RemoveJobCommand).WasHandled;
        }

        private void CreateJobCommand(CreatePersistentJob createJob)
        {
            if (createJob.To == null)
            {
                Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException("createJob.To")));
            }
            if (createJob.Trigger == null)
            {
                Context.Sender.Tell(new CreateJobFail(null, null, new ArgumentNullException("createJob.Trigger")));
            }
            else
            {

                try
                {
                    var job =
                    QuartzPersistentJob.CreateBuilderWithData(createJob.To, createJob.Message, Context.System)
                        .WithIdentity(createJob.Trigger.JobKey)
                        .Build();
                    Scheduler.ScheduleJob(job, createJob.Trigger);

                    Context.Sender.Tell(new JobCreated(createJob.Trigger.JobKey, createJob.Trigger.Key));
                }
                catch (Exception ex)
                {
                    Context.Sender.Tell(new CreateJobFail(createJob.Trigger.JobKey, createJob.Trigger.Key, ex));
                }
            }
        }
    }
}
