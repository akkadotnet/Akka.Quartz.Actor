using Akka.Actor;
using Quartz;

namespace Akka.Quartz.Actor.Commands
{
    /// <summary>
    ///     Message to add a trigger.
    /// </summary>
    public class CreatePersistentJob : IJobCommand
    {
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
        ///     Message to be sent to the destination actor
        /// </summary>
        public object Message { get; private set; }

        /// <summary>
        ///     Schedule job execution trigger 
        /// </summary>
        public ITrigger Trigger { get; private set; }
    
        /// <summary>
        ///     Schedule job options 
        /// </summary>
        public ScheduleJobOptions Options { get; private set; }
    }
}