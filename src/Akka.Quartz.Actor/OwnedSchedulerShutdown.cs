using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Quartz;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor
{
    // One system task tracks only live/pending schedulers, rather than retaining a task per stopped actor.
    internal sealed class OwnedSchedulerShutdown : IExtension
    {
        private readonly object _gate = new object();
        private readonly HashSet<OwnedScheduler> _owned = new HashSet<OwnedScheduler>();
        private readonly ILoggingAdapter _log;
        private bool _stopping;

        public OwnedSchedulerShutdown(ActorSystem system)
        {
            _log = Logging.GetLogger(system, typeof(OwnedSchedulerShutdown));
            CoordinatedShutdown.Get(system).AddTask(CoordinatedShutdown.PhaseBeforeActorSystemTerminate,
                "quartz-owned-schedulers", ShutdownAsync);
        }

        public OwnedScheduler Register(NameValueCollection properties)
        {
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("Cannot create an owned Quartz scheduler during coordinated shutdown.");
                var instanceName = properties.Get(QuartzActor.PropertySchedulerInstanceName);
                // A restarted actor reuses its instance name and job store. Starting before the previous
                // incarnation has drained would let two schedulers recover and acquire the same triggers.
                var predecessors = _owned
                    .Where(other => other.IsStopping && string.Equals(other.InstanceName, instanceName, StringComparison.Ordinal))
                    .Select(other => other.Completion)
                    .ToArray();
                var owned = new OwnedScheduler(this, instanceName, QuartzSchedulerBuilder.Create().UseProperties(properties).Build(), predecessors);
                _owned.Add(owned);
                return owned;
            }
        }

        private async Task<Done> ShutdownAsync()
        {
            OwnedScheduler[] pending;
            lock (_gate)
            {
                _stopping = true;
                pending = _owned.ToArray();
            }
            await Task.WhenAll(pending.Select(owned => owned.StopAsync())).ConfigureAwait(false);
            return Done.Instance;
        }

        internal sealed class OwnedScheduler
        {
            private readonly OwnedSchedulerShutdown _owner;
            private readonly StandaloneSchedulerFactory _factory;
            private readonly object _gate = new object();
            private Task _startup = Task.CompletedTask;
            private bool _stopping;
            private readonly Lazy<Task> _shutdown;
            private readonly TaskCompletionSource<Done> _completion = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<IScheduler> Scheduler { get; }
            public string InstanceName { get; }

            /// <summary>Completes, successfully or not, once this scheduler has released its job store.</summary>
            public Task Completion => _completion.Task;

            public bool IsStopping
            {
                get { lock (_gate) return _stopping; }
            }

            public OwnedScheduler(OwnedSchedulerShutdown owner, string instanceName, StandaloneSchedulerFactory factory, Task[] predecessors)
            {
                _owner = owner;
                InstanceName = instanceName;
                _factory = factory;
                // Quartz/plugins must finish independently of ActorTaskScheduler, whose mailbox can stop first.
                Scheduler = Task.Run(async () =>
                {
                    // Completion never faults: a failed predecessor shutdown is reported by whoever stopped it.
                    await Task.WhenAll(predecessors).ConfigureAwait(false);
                    return await factory.GetScheduler().ConfigureAwait(false);
                });
                _shutdown = new Lazy<Task>(() => Task.Run(DisposeAsync));
            }

            public Task StartAsync(IScheduler scheduler)
            {
                lock (_gate)
                {
                    if (_stopping) throw new InvalidOperationException("Owned Quartz scheduler is shutting down.");
                    _startup = Task.Run(async () => await scheduler.Start().ConfigureAwait(false));
                    return _startup;
                }
            }

            public Task StopAsync()
            {
                lock (_gate) _stopping = true;
                return _shutdown.Value;
            }

            private async Task DisposeAsync()
            {
                IScheduler scheduler = null;
                Task startup;
                lock (_gate) startup = _startup;
                try
                {
                    scheduler = await Scheduler.ConfigureAwait(false);
                    await startup.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _owner._log.Error(exception, "Owned Quartz scheduler initialization or startup failed; releasing its resources.");
                }
                try
                {
                    try
                    {
                        if (scheduler != null) await scheduler.Shutdown(waitForJobsToComplete: true).ConfigureAwait(false);
                    }
                    finally
                    {
                        await _factory.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    // A failed shutdown must not keep this entry alive for the rest of the ActorSystem's life.
                    lock (_owner._gate) _owner._owned.Remove(this);
                    _completion.TrySetResult(Done.Instance);
                }
            }
        }
    }

    internal sealed class OwnedSchedulerShutdownExtension : ExtensionIdProvider<OwnedSchedulerShutdown>
    {
        public override OwnedSchedulerShutdown CreateExtension(ExtendedActorSystem system) => new OwnedSchedulerShutdown(system);
    }
}
