using System;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Quartz.Actor.Commands;
using Akka.Quartz.Actor.Events;
using Quartz;
using Xunit;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Collections.Specialized;
using Quartz.Impl.AdoJobStore.Common;
using System.Data;
using Quartz.Impl;

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
            var properties = new NameValueCollection
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

            StandaloneSchedulerFactory sf = QuartzSchedulerBuilder.Create().UseProperties(properties).Build();
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
