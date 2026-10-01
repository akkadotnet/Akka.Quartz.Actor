using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Quartz;
using IScheduler = Quartz.IScheduler;

namespace Akka.Quartz.Actor
{
    // One system task tracks only live/pending schedulers, rather than retaining a task per stopped actor.
    internal sealed class OwnedSchedulerShutdown : IExtension
    {
        private readonly object _gate = new object();
        private readonly HashSet<OwnedScheduler> _owned = new HashSet<OwnedScheduler>();
        private bool _stopping;

        public OwnedSchedulerShutdown(ActorSystem system)
        {
            CoordinatedShutdown.Get(system).AddTask(CoordinatedShutdown.PhaseBeforeActorSystemTerminate,
                "quartz-owned-schedulers", ShutdownAsync);
        }

        public OwnedScheduler Register()
        {
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("Cannot create an owned Quartz scheduler during coordinated shutdown.");
                var owned = new OwnedScheduler(this);
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
            private readonly TaskCompletionSource<(StandaloneSchedulerFactory Factory, IScheduler Scheduler)> _initialized =
                new TaskCompletionSource<(StandaloneSchedulerFactory, IScheduler)>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Lazy<Task> _shutdown;

            public OwnedScheduler(OwnedSchedulerShutdown owner)
            {
                _owner = owner;
                _shutdown = new Lazy<Task>(DisposeAsync);
            }

            public void Initialized(StandaloneSchedulerFactory factory, IScheduler scheduler) =>
                _initialized.TrySetResult((factory, scheduler));

            public Task StopAsync() => _shutdown.Value;

            private async Task DisposeAsync()
            {
                var (factory, scheduler) = await _initialized.Task.ConfigureAwait(false);
                if (scheduler != null) await scheduler.Shutdown(waitForJobsToComplete: true).ConfigureAwait(false);
                if (factory != null) await factory.DisposeAsync().ConfigureAwait(false);
                lock (_owner._gate) _owner._owned.Remove(this);
            }
        }
    }

    internal sealed class OwnedSchedulerShutdownExtension : ExtensionIdProvider<OwnedSchedulerShutdown>
    {
        public override OwnedSchedulerShutdown CreateExtension(ExtendedActorSystem system) => new OwnedSchedulerShutdown(system);
    }
}
