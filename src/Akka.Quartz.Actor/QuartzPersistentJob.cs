using Akka.Actor;
using Akka.Util.Internal;
using Quartz;
using Akka.Serialization;
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
        private const string ManifestKey = "manifest";
        public const string SysKey = "sys";

        public Task Execute(IJobExecutionContext context)
        {
            var jdm = context.JobDetail.JobDataMap;
            if (jdm.ContainsKey(MessageKey) && jdm.ContainsKey(ActorKey))
            {
                if (jdm[ActorKey] is string actorPath && context.Scheduler.Context[SysKey] is ActorSystem sys)
                {
                    ActorSelection selection = sys.ActorSelection(actorPath);
                    byte[] messageBytes = jdm[MessageKey] as byte[];

                    var serializer = sys.Serialization.FindSerializerForType(typeof(object));
                    object message;

                    if (jdm.ContainsKey(ManifestKey) && !string.IsNullOrEmpty(jdm[ManifestKey] as string))
                    {
                        var manifest = jdm[ManifestKey] as string;

                        if (serializer is SerializerWithStringManifest sm)
                        {
                            message = sm.FromBinary(messageBytes, manifest);
                        }
                        else
                        {
                            var type = System.Type.GetType(manifest);
                            if (type == null) type = typeof(object);

                            message = serializer.FromBinary(messageBytes, type);
                        }
                    }
                    else
                    {
                        message = serializer.FromBinary(messageBytes, typeof(object));
                    }

                    selection.Tell(message);
                }
            }

            return Task.CompletedTask;
        }

        public static JobBuilder CreateBuilderWithData(ActorPath actorPath, object message, ActorSystem system)
        {
            Serializer messageSerializer = system.Serialization.FindSerializerForType(typeof(object));
            var manifest = Serialization.Serialization.ManifestFor(messageSerializer, message);
            var serializedMessage = messageSerializer.ToBinary(message);
            var serializedPath = actorPath.ToSerializationFormat();

            var jdm = new JobDataMap();
            jdm
                .AddAndReturn(MessageKey, serializedMessage)
                .AddAndReturn(ActorKey, serializedPath)
                .Add(ManifestKey, manifest);

            return JobBuilder.Create<QuartzPersistentJob>().UsingJobData(jdm);
        }
    }
}
