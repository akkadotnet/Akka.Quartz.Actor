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
                var owned = new OwnedScheduler(this, QuartzSchedulerBuilder.Create().UseProperties(properties).Build());
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
            public Task<IScheduler> Scheduler { get; }

            public OwnedScheduler(OwnedSchedulerShutdown owner, StandaloneSchedulerFactory factory)
            {
                _owner = owner;
                _factory = factory;
                // Quartz/plugins must finish independently of ActorTaskScheduler, whose mailbox can stop first.
                Scheduler = Task.Run(async () => await factory.GetScheduler().ConfigureAwait(false));
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
                    if (scheduler != null) await scheduler.Shutdown(waitForJobsToComplete: true).ConfigureAwait(false);
                }
                finally
                {
                    await _factory.DisposeAsync().ConfigureAwait(false);
                }
                lock (_owner._gate) _owner._owned.Remove(this);
            }
        }
    }

    internal sealed class OwnedSchedulerShutdownExtension : ExtensionIdProvider<OwnedSchedulerShutdown>
    {
        public override OwnedSchedulerShutdown CreateExtension(ExtendedActorSystem system) => new OwnedSchedulerShutdown(system);
    }
}
