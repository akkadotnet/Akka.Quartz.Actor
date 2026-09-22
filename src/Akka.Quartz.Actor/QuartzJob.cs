using Akka.Actor;
using Akka.Util.Internal;
using Quartz;
using System.Threading;
using System.Threading.Tasks;

namespace Akka.Quartz.Actor
{
    /// <summary>
    /// Job
    /// </summary>
    public class QuartzJob : IJob
    {
        private const string MessageKey = "message";
        private const string ActorKey = "actor";

        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            var jdm = context.JobDetail.JobDataMap;
            if (jdm.ContainsKey(MessageKey) && jdm.ContainsKey(ActorKey))
            {
                if (jdm[ActorKey] is IActorRef actor)
                {
                    actor.Tell(jdm[MessageKey]);
                }
            }
            return ValueTask.CompletedTask;
        }

        public static JobBuilder<QuartzJob> CreateBuilderWithData(IActorRef actorRef, object message)
        {
            var jdm = new JobDataMap();
            jdm.AddAndReturn(MessageKey, message).Add(ActorKey, actorRef);
            return JobBuilder.Create<QuartzJob>().UsingJobData(jdm);
        }
    }
}