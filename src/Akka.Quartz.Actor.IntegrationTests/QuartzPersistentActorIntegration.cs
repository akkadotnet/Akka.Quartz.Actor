using System;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Xunit;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Collections.Specialized;

namespace Akka.Quartz.Actor.IntegrationTests
{
    public class QuartzPersistentActorIntegration : TestKit.Xunit.TestKit, IClassFixture<QuartzPersistentActorIntegration.SqliteFixture>
    {
        private SqliteFixture _fixture;
        public QuartzPersistentActorIntegration(ITestOutputHelper output, SqliteFixture fixture)
            : base(nameof(QuartzPersistentActorIntegration), output)
        {
            _fixture = fixture;
        }
        
        [Fact]
        public async Task QuartzPersistentActor_DB_Should_Create_Job()
        {
            StandaloneSchedulerFactory sf = QuartzSchedulerBuilder.Create().UseProperties(StoreProperties()).Build();
            var sched = await sf.GetScheduler(TestContext.Current.CancellationToken);
            await sched.Start(TestContext.Current.CancellationToken);

            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor(sched)), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, new { Greeting = "hello" }, TriggerBuilder.Create().WithCronSchedule("0/5 * * * * ?").Build()));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            probe.ExpectMsg(new { Greeting = "hello" }, TimeSpan.FromSeconds(7), cancellationToken: TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
            probe.ExpectMsg(new { Greeting = "hello" }, cancellationToken: TestContext.Current.CancellationToken);
            Sys.Stop(quartzActor);
            await sf.DisposeAsync();
        }

        /// <summary>
        /// Replaces a stored job by sending <see cref="RemoveJob"/> and then <see cref="CreatePersistentJob"/>
        /// for the same keys, without waiting for <see cref="JobRemoved"/> in between.
        /// See https://github.com/akkadotnet/Akka.Quartz.Actor/issues/374
        /// </summary>
        [Theory]
        [InlineData("0 0 0 1 1 ?")] // idle job
        [InlineData("* * * * * ?")] // job that fires while it is being replaced
        public async Task QuartzPersistentActor_DB_Should_Create_Job_Right_After_Removing_It(string cron)
        {
            var jobKey = new JobKey("remove-then-create");
            var triggerKey = new TriggerKey("remove-then-create");
            ITrigger Trigger() => TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey).WithCronSchedule(cron).Build();

            StandaloneSchedulerFactory sf = QuartzSchedulerBuilder.Create().UseProperties(StoreProperties()).Build();
            var sched = await sf.GetScheduler(TestContext.Current.CancellationToken);
            await sched.Start(TestContext.Current.CancellationToken);

            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor(sched)), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello", Trigger()));
            ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);

            for (var round = 0; round < 50; round++)
            {
                quartzActor.Tell(new RemoveJob(jobKey, triggerKey));
                quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, "Hello", Trigger()));
                ExpectMsg<JobRemoved>(cancellationToken: TestContext.Current.CancellationToken);
                ExpectMsg<JobCreated>(cancellationToken: TestContext.Current.CancellationToken);
            }

            // the store outlives this test, so leave nothing behind for the next one
            quartzActor.Tell(new RemoveJob(jobKey, triggerKey));
            ExpectMsg<JobRemoved>(cancellationToken: TestContext.Current.CancellationToken);
            Sys.Stop(quartzActor);
            await sf.DisposeAsync();
        }

        private static NameValueCollection StoreProperties() => new NameValueCollection
        {
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.LocalTransactionJobStore, Quartz",
            ["quartz.jobStore.useProperties"] = "false",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.jobStore.tablePrefix"] = "qrtz_",
            ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
            ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
            ["quartz.dataSource.default.connectionString"] = "Data Source=quartz-jobs.db",
            ["quartz.serializer.type"] = "newtonsoft"
        };

        public class SqliteFixture : IDisposable
        {
            private const string DatabaseFileName = "quartz-jobs.db";

            public SqliteFixture()
            {
                if (File.Exists(DatabaseFileName))
                {
                    File.Delete(DatabaseFileName);
                }

                var script = File.ReadAllText("tables_sqlite.sql");

                using (var dbConnection = new SqliteConnection($"Data Source={DatabaseFileName};"))
                {
                    using (var command = new SqliteCommand(script, dbConnection))
                    {
                        dbConnection.Open();
                        command.ExecuteNonQuery();
                    }
                }
            }

            public void Dispose()
            {
            }
        }

    }
}
