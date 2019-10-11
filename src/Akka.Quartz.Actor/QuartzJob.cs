using Akka.Actor;
using Akka.Util.Internal;
using Quartz;
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

        public Task Execute(IJobExecutionContext context)
        {
            var jdm = context.JobDetail.JobDataMap;
            if (jdm.ContainsKey(MessageKey) && jdm.ContainsKey(ActorKey))
            {
                if (jdm[ActorKey] is IActorRef actor)
                {
                    actor.Tell(jdm[MessageKey]);
                }
            }
            return Task.CompletedTask;
        }

        public static JobBuilder CreateBuilderWithData(IActorRef actorRef, object message)
        {
            var jdm = new JobDataMap();
            jdm.AddAndReturn(MessageKey, message).Add(ActorKey, actorRef);
            return JobBuilder.Create<QuartzJob>().UsingJobData(jdm);
        }
    }
}