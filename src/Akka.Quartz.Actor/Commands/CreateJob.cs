using Akka.Actor;
using Quartz;

namespace Akka.Quartz.Actor.Commands
{
    /// <summary>
    ///     Message to add a trigger.
    /// </summary>
    public class CreateJob(IActorRef to, object message, ITrigger trigger, ScheduleJobOptions options = default)
        : IJobCommand
    {
        /// <summary>
        ///     The destination actor
        /// </summary>
        public IActorRef To { get; private set; } = to;

        /// <summary>
        ///     Message to be sent to the destination actor
        /// </summary>
        public object Message { get; private set; } = message;

        /// <summary>
        ///     Schedule job execution trigger 
        /// </summary>
        public ITrigger Trigger { get; private set; } = trigger;

        /// <summary>
        ///     Schedule job options 
        /// </summary>
        public ScheduleJobOptions Options { get; private set; } = options;
    }
}