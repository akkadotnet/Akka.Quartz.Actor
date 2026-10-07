using Quartz;

namespace Akka.Quartz.Actor.Commands
{
    /// <summary>
    ///     Message to remove a cron scheduler.
    /// </summary>
    public class RemoveJob : IJobCommand
    {
        public RemoveJob(JobKey jobKey, TriggerKey triggerKey)
        {
            JobKey = jobKey;
            TriggerKey = triggerKey;
        }

        /// <summary>
        ///     Job key
        /// </summary>
        public JobKey JobKey { get; private set; }

        /// <summary>
        ///     Trigger key
        /// </summary>
        public TriggerKey TriggerKey { get; private set; }
    }
}