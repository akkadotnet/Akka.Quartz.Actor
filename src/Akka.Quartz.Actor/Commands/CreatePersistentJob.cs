using Akka.Actor;
using Quartz;

namespace Akka.Quartz.Actor.Commands
{
    /// <summary>
    ///     Message to add a trigger.
    /// </summary>
    public class CreatePersistentJob(
        ActorPath to,
        object message,
        ITrigger trigger,
        ScheduleJobOptions options = default)
        : IJobCommand
    {
        /// <summary>
        ///     The desination actor
        /// </summary>
        public ActorPath To { get; private set; } = to;

        /// <summary>
        ///     Message
        /// </summary>
        public object Message { get; private set; } = message;

        /// <summary>
        ///     Trigger 
        /// </summary>
        public ITrigger Trigger { get; private set; } = trigger;

        /// <summary>
        ///     Schedule job options 
        /// </summary>
        public ScheduleJobOptions Options { get; private set; } = options;
    }
}