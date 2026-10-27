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
        /// <remarks>
        /// <para>
        /// <see cref="ScheduleJobOptions"/> has a single property, <see cref="ScheduleJobOptions.Replace"/>, which can also be
        /// specified using the static property <see cref="ScheduleJobOptions.Replacing"/>.
        /// </para>
        /// <para>
        /// When <c>Replace</c> is false (the default), scheduling a job or trigger whose key already exists throws
        /// <see cref="ObjectAlreadyExistsException"/>.
        /// </para>
        /// <para>
        /// When <c>Replace</c> is true, Quartz overwrites any already stored job and trigger with the same keys in a single
        /// operation under the store's lock. Replacement matches on keys: a trigger is replaced only when its trigger key
        /// matches, and a job only when its job key matches. Replacing a trigger does not delete other triggers that point
        /// at the same job, so a job scheduled under two trigger keys keeps firing on both. To replace a job rather than add
        /// a second schedule to it, reuse the same job and trigger keys.
        /// </para>
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
        /// <remarks>
        /// <para>
        /// <see cref="ScheduleJobOptions"/> has a single property, <see cref="ScheduleJobOptions.Replace"/>, which can also be
        /// specified using the static property <see cref="ScheduleJobOptions.Replacing"/>.
        /// </para>
        /// <para>
        /// When <c>Replace</c> is false (the default), scheduling a job or trigger whose key already exists throws
        /// <see cref="ObjectAlreadyExistsException"/>.
        /// </para>
        /// <para>
        /// When <c>Replace</c> is true, Quartz overwrites any already stored job and trigger with the same keys in a single
        /// operation under the store's lock. Replacement matches on keys: a trigger is replaced only when its trigger key
        /// matches, and a job only when its job key matches. Replacing a trigger does not delete other triggers that point
        /// at the same job, so a job scheduled under two trigger keys keeps firing on both. To replace a job rather than add
        /// a second schedule to it, reuse the same job and trigger keys.
        /// </para>
        /// </remarks>
        public ScheduleJobOptions Options { get; private set; }
    }
}