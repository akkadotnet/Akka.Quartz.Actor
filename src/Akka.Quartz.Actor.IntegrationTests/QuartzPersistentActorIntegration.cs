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
using Xunit.Abstractions;

namespace Akka.Quartz.Actor.IntegrationTests
{
    public class QuartzPersistentActorIntegration : TestKit.Xunit2.TestKit, IClassFixture<QuartzPersistentActorIntegration.SqliteFixture>
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
            DbProvider.RegisterDbMetadata("sqlite-custom", new DbMetadata()
            {
                AssemblyName = typeof(SqliteConnection).Assembly.GetName().Name,
                ConnectionType = typeof(SqliteConnection),
                CommandType = typeof(SqliteCommand),
                ParameterType = typeof(SqliteParameter),
                ParameterDbType = typeof(DbType),
                ParameterDbTypePropertyName = "DbType",
                ParameterNamePrefix = "@",
                ExceptionType = typeof(SqliteException),
                BindByName = true
            });

            var properties = new NameValueCollection
            {
                ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
                ["quartz.jobStore.useProperties"] = "false",
                ["quartz.jobStore.dataSource"] = "default",
                ["quartz.jobStore.tablePrefix"] = "qrtz_",
                ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
                ["quartz.dataSource.default.provider"] = "sqlite-custom",
                ["quartz.dataSource.default.connectionString"] = "Data Source=quartz-jobs.db",
                ["quartz.jobStore.lockHandler.type"] = "Quartz.Impl.AdoJobStore.UpdateLockRowSemaphore, Quartz",
                ["quartz.serializer.type"] = "binary"
            };

            ISchedulerFactory sf = new StdSchedulerFactory(properties);
            var sched = await sf.GetScheduler();
            await sched.Start();

            var probe = CreateTestProbe(Sys);
            var quartzActor = Sys.ActorOf(Props.Create(() => new QuartzPersistentActor(sched)), "QuartzActor");
            quartzActor.Tell(new CreatePersistentJob(probe.Ref.Path, new { Greeting = "hello" }, TriggerBuilder.Create().WithCronSchedule("0/5 * * * * ?").Build()));
            ExpectMsg<JobCreated>();
            probe.ExpectMsg(new { Greeting = "hello" }, TimeSpan.FromSeconds(7));
            await Task.Delay(TimeSpan.FromSeconds(7));
            probe.ExpectMsg(new { Greeting = "hello" });
            Sys.Stop(quartzActor);
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
