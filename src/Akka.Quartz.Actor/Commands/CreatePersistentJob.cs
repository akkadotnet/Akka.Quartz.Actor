using Akka.Actor;
using Quartz;

namespace Akka.Quartz.Actor.Commands
{
    /// <summary>
    ///     Message to add a trigger.
    /// </summary>
    public class CreatePersistentJob : IJobCommand
    {
        /// <summary>
        /// Create a persistent scheduler job 
        /// </summary>
        /// <param name="to">The destination actor</param>
        /// <param name="message">Message to be scheduled</param>
        /// <param name="trigger">Job execution trigger</param>
        /// <param name="options">Job options</param>
        /// <remarks>ScheduleJobOptions has single property Replace (can also be specified using static method ScheduleJobOptions.Replacing).
        /// When set to false (the default), attempting to schedule a job or trigger with a key that already exists throws an ObjectAlreadyExistsException.
        /// When set to true, it overwrites any already stored jobs and/or triggers with the same keys in a single operation under the store's lock.
        /// </remarks>
        public CreatePersistentJob(ActorPath to, object message, ITrigger trigger, ScheduleJobOptions options = default)
        {
            To = to;
            Message = message;
            Trigger = trigger;
            Options = options;
        }
        
        /// <summary>
        ///     The destination actor
        /// </summary>
        public ActorPath To { get; private set; }

        /// <summary>
        ///     Message to be scheduled
        /// </summary>
        public object Message { get; private set; }

        /// <summary>
        ///     Job execution trigger 
        /// </summary>
        public ITrigger Trigger { get; private set; }
    
        /// <summary>
        ///     Job options 
        /// </summary>
        /// <remarks>ScheduleJobOptions has single property Replace (can also be specified using static method ScheduleJobOptions.Replacing).
        /// When set to false (the default), attempting to schedule a job or trigger with a key that already exists throws an ObjectAlreadyExistsException.
        /// When set to true, it overwrites any already stored jobs and/or triggers with the same keys in a single operation under the store's lock.
        /// </remarks>
        public ScheduleJobOptions Options { get; private set; }
    }
}