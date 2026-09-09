using System;
using Akka.Actor;
using Akka.Util.Internal;
using Quartz;
using Akka.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Akka.Quartz.Actor
{
    /// <summary>
    /// Persistent Job
    /// </summary>
    public class QuartzPersistentJob : IJob
    {
        private const string MessageKey = "message";
        private const string ActorKey = "actor";
        public const string SysKey = "sys";

        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {

            var jdm = context.JobDetail.JobDataMap;
            if (jdm.ContainsKey(MessageKey) && jdm.ContainsKey(ActorKey))
            {
                if (jdm[ActorKey] is string actorPath && jdm[MessageKey] is string serializedMessage && context.Scheduler.Context[SysKey] is ActorSystem sys)
                {
                    ActorSelection selection = sys.ActorSelection(actorPath);
                    byte[] messageBytes = Convert.FromBase64String(serializedMessage);
                    var message = sys.Serialization.FindSerializerForType(typeof(object)).FromBinary(messageBytes, typeof(object));
                    selection.Tell(message);
                }
            }

            return ValueTask.CompletedTask;
        }

        public static JobBuilder<QuartzPersistentJob> CreateBuilderWithData(ActorPath actorPath, object message, ActorSystem system)
        {
            Serializer messageSerializer = system.Serialization.FindSerializerForType(typeof(object));
            var serializedMessage = Convert.ToBase64String(messageSerializer.ToBinary(message));
            var serializedPath = actorPath.ToSerializationFormat();
            var jdm = new JobDataMap();
            jdm.AddAndReturn(MessageKey, serializedMessage).Add(ActorKey, serializedPath);
            return JobBuilder.Create<QuartzPersistentJob>().UsingJobData(jdm);
        }
    }
}
