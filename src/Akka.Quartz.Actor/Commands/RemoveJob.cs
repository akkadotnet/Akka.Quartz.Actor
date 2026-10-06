using Quartz;

namespace Akka.Quartz.Actor.Commands
{
    /// <summary>
    ///     Message to remove a cron scheduler.
    /// </summary>
    public class RemoveJob(JobKey jobKey, TriggerKey triggerKey) : IJobCommand
    {
        /// <summary>
        ///     Job key
        /// </summary>
        public JobKey JobKey { get; private set; } = jobKey;

        /// <summary>
        ///     Trigger key
        /// </summary>
        public TriggerKey TriggerKey { get; private set; } = triggerKey;
    }
}