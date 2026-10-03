using System.Collections;
using System.Collections.Specialized;
using Microsoft.Data.Sqlite;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Calendar;
using Quartz.Simpl;
using Quartz3Migration;
using Xunit;

namespace Quartz3Migration.Tests;

public sealed class ConversionTests
{
    [Fact]
    public async Task Dry_Run_Should_Validate_All_Blob_Locations_Without_Writing()
    {
        await using var store = await Fixture.Create();
        var before = await store.Blob("JOB_DETAILS", "JOB_DATA");
        var report = await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", false, TestContext.Current.CancellationToken);
        Assert.Equal(3, report.BinaryBlobs);
        Assert.False(report.Applied);
        Assert.Equal(before, await store.Blob("JOB_DETAILS", "JOB_DATA"));
        Assert.Equal(store.Schedule, await store.Scalar("SELECT START_TIME || ':' || NEXT_FIRE_TIME || ':' || MISFIRE_INSTR FROM QRTZ_TRIGGERS"));
    }

    [Fact]
    public async Task Apply_Should_Preserve_Message_Bytes_And_Schedules_And_Be_Idempotent()
    {
        await using var store = await Fixture.Create();
        var report = await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        Assert.Equal(3, report.BinaryBlobs);
        Assert.True(report.Applied);
        var json = new JsonObjectSerializer();
        json.Initialize();
        var map = json.DeSerialize<IDictionary>(await store.Blob("JOB_DETAILS", "JOB_DATA"))!;
        Assert.Equal(store.Message, Assert.IsType<byte[]>(map["message"]));
        Assert.Equal(123L, Convert.ToInt64(json.DeSerialize<IDictionary>(await store.Blob("TRIGGERS", "JOB_DATA"))!["count"]));
        Assert.IsType<HolidayCalendar>(json.DeSerialize<ICalendar>(await store.Blob("CALENDARS", "CALENDAR")));
        Assert.Equal(store.Schedule, await store.Scalar("SELECT START_TIME || ':' || NEXT_FIRE_TIME || ':' || MISFIRE_INSTR FROM QRTZ_TRIGGERS"));
        var second = await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        Assert.Equal(0, second.BinaryBlobs);
        Assert.Equal(3, second.JsonBlobs);
    }

    [Fact]
    public async Task Invalid_Later_Blob_Should_Roll_Back_Earlier_Updates()
    {
        await using var store = await Fixture.Create();
        var before = await store.Blob("JOB_DETAILS", "JOB_DATA");
        await store.Execute("UPDATE QRTZ_CALENDARS SET CALENDAR=x'00010000'");
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken));
        Assert.Equal(before, await store.Blob("JOB_DETAILS", "JOB_DATA"));
    }

    [Fact]
    public async Task Scheduler_Scope_Should_Leave_Other_Schedulers_And_Existing_Json_Unchanged()
    {
        await using var store = await Fixture.Create();
        var original = await store.Blob("JOB_DETAILS", "JOB_DATA");
        await store.Execute("INSERT INTO QRTZ_JOB_DETAILS SELECT 'other',JOB_NAME,JOB_GROUP,DESCRIPTION,JOB_CLASS_NAME,IS_DURABLE,IS_NONCONCURRENT,IS_UPDATE_DATA,REQUESTS_RECOVERY,JOB_DATA FROM QRTZ_JOB_DETAILS;");
        var json = new JsonObjectSerializer();
        json.Initialize();
        var existingJson = json.Serialize(new Dictionary<string, object> { ["existing"] = "json" });
        await store.WriteBlob("INSERT INTO QRTZ_JOB_DETAILS SELECT SCHED_NAME,'json-job',JOB_GROUP,DESCRIPTION,JOB_CLASS_NAME,IS_DURABLE,IS_NONCONCURRENT,IS_UPDATE_DATA,REQUESTS_RECOVERY,@blob FROM QRTZ_JOB_DETAILS WHERE SCHED_NAME='QuartzScheduler'", existingJson);
        var report = await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.JsonBlobs);
        Assert.Equal(original, await store.Blob("JOB_DETAILS", "JOB_DATA", "SCHED_NAME='other'"));
        Assert.Equal(existingJson, await store.Blob("JOB_DETAILS", "JOB_DATA", "JOB_NAME='json-job'"));
    }

    [Fact]
    public async Task Converted_Data_Should_Be_Readable_By_A_Real_Quartz3_Scheduler()
    {
        await using var store = await Fixture.Create();
        await store.Execute($"UPDATE QRTZ_JOB_DETAILS SET JOB_CLASS_NAME='{typeof(NoopJob).AssemblyQualifiedName}'");
        await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "QuartzScheduler",
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
            ["quartz.jobStore.useProperties"] = "false",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
            ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
            ["quartz.dataSource.default.connectionString"] = store.Connection.ConnectionString,
            ["quartz.serializer.type"] = "newtonsoft"
        });
        var scheduler = await factory.GetScheduler(TestContext.Current.CancellationToken);
        try
        {
            var saved = await scheduler.GetJobDetail(new JobKey("legacy-job"), TestContext.Current.CancellationToken);
            Assert.Equal(store.Message, Assert.IsType<byte[]>(saved!.JobDataMap["message"]));
            Assert.NotNull(await scheduler.GetTrigger(new TriggerKey("legacy-trigger"), TestContext.Current.CancellationToken));
            Assert.IsType<HolidayCalendar>(await scheduler.GetCalendar("holiday", TestContext.Current.CancellationToken));
        }
        finally { await scheduler.Shutdown(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Custom_Blob_Triggers_Should_Require_Application_Specific_Migration_And_Roll_Back()
    {
        await using var store = await Fixture.Create();
        var before = await store.Blob("JOB_DETAILS", "JOB_DATA");
        await store.Execute("INSERT INTO QRTZ_BLOB_TRIGGERS VALUES ('QuartzScheduler','legacy-trigger','DEFAULT',x'01')");
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken));
        Assert.Equal(before, await store.Blob("JOB_DETAILS", "JOB_DATA"));
    }

    [Fact]
    public async Task Empty_Blob_Should_Be_Left_Empty()
    {
        await using var store = await Fixture.Create();
        await store.Execute("UPDATE QRTZ_TRIGGERS SET JOB_DATA=x''");
        var report = await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        Assert.Equal(1, report.EmptyBlobs);
        Assert.Empty(await store.Blob("TRIGGERS", "JOB_DATA"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_Object_State_Should_Be_Rejected_Without_Committing_Earlier_Rows(bool apply)
    {
        await using var store = await Fixture.Create();
        var before = await store.Blob("JOB_DETAILS", "JOB_DATA");
        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        var state = new PrivateState("important persisted value");
        Assert.Equal("important persisted value", state.Read());
        var unsupported = binary.Serialize(JobData(new Dictionary<string, object> { ["custom"] = state }));
        await store.WriteBlob("UPDATE QRTZ_TRIGGERS SET JOB_DATA=@blob", unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", apply, TestContext.Current.CancellationToken));
        Assert.Equal(before, await store.Blob("JOB_DETAILS", "JOB_DATA"));
        Assert.Equal(unsupported, await store.Blob("TRIGGERS", "JOB_DATA"));
    }

    [Fact]
    public async Task Timestamp_String_Type_Change_Should_Be_Rejected_And_Rolled_Back()
    {
        await using var store = await Fixture.Create();
        var before = await store.Blob("JOB_DETAILS", "JOB_DATA");
        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        var original = binary.Serialize(JobData(new Dictionary<string, object> { ["text"] = "2026-09-30T00:00:00+00:00" }));
        await store.WriteBlob("UPDATE QRTZ_TRIGGERS SET JOB_DATA=@blob", original);
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken));
        Assert.Equal(before, await store.Blob("JOB_DETAILS", "JOB_DATA"));
        Assert.Equal(original, await store.Blob("TRIGGERS", "JOB_DATA"));
    }

    [Fact]
    public async Task Custom_Map_Comparer_Should_Be_Rejected_Without_Writing()
    {
        await using var store = await Fixture.Create();
        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        var original = binary.Serialize(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["name"] = "value" });
        await store.WriteBlob("UPDATE QRTZ_JOB_DETAILS SET JOB_DATA=@blob", original);
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken));
        Assert.Equal(original, await store.Blob("JOB_DETAILS", "JOB_DATA"));
    }

    [Serializable]
    public sealed class PrivateState
    {
        private readonly string _value;
        public PrivateState(string value) => _value = value;
        public string Read() => _value;
    }

    [Fact]
    public async Task Nonempty_Holiday_Calendar_Should_Preserve_Its_Exclusions()
    {
        await using var store = await Fixture.Create();
        var excluded = new DateTimeOffset(2030, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var calendar = new HolidayCalendar { TimeZone = TimeZoneInfo.Utc, Description = "holiday exclusion" };
        calendar.AddExcludedDate(excluded.UtcDateTime);
        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        await store.WriteBlob("UPDATE QRTZ_CALENDARS SET CALENDAR=@blob", binary.Serialize(calendar));
        await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        var json = new JsonObjectSerializer();
        json.Initialize();
        var recovered = Assert.IsType<HolidayCalendar>(json.DeSerialize<ICalendar>(await store.Blob("CALENDARS", "CALENDAR")));
        Assert.False(recovered.IsTimeIncluded(excluded));
        Assert.True(recovered.IsTimeIncluded(excluded.AddDays(1)));
        Assert.Equal(calendar.Description, recovered.Description);
        Assert.Equal(calendar.TimeZone.Id, recovered.TimeZone.Id);
    }

    [Fact]
    public async Task Supported_Map_Values_Should_Preserve_Content()
    {
        await using var store = await Fixture.Create();
        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        await store.WriteBlob("UPDATE QRTZ_TRIGGERS SET JOB_DATA=@blob", binary.Serialize(JobData(new Dictionary<string, object>
        {
            ["null"] = null!, ["text"] = "plain text", ["bool"] = true,
            ["integer"] = long.MinValue, ["bytes"] = new byte[] { 0, 128, 255 }
        })));
        await BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken);
        var json = new JsonObjectSerializer();
        json.Initialize();
        var recovered = json.DeSerialize<IDictionary>(await store.Blob("TRIGGERS", "JOB_DATA"))!;
        Assert.Equal(5, recovered.Count);
        Assert.Null(recovered["null"]);
        Assert.Equal("plain text", recovered["text"]);
        Assert.Equal(true, recovered["bool"]);
        Assert.Equal(long.MinValue, recovered["integer"]);
        Assert.Equal(new byte[] { 0, 128, 255 }, Assert.IsType<byte[]>(recovered["bytes"]));
    }

    [Fact]
    public async Task Daily_Calendar_Precision_Change_Should_Be_Rejected_And_Rolled_Back()
    {
        await using var store = await Fixture.Create();
        var original = await store.Blob("JOB_DETAILS", "JOB_DATA");
        var binary = new BinaryObjectSerializer();
        binary.Initialize();
        var calendar = new DailyCalendar("09:00", "17:00") { TimeZone = TimeZoneInfo.Utc };
        var saved = binary.Serialize(calendar);
        await store.WriteBlob("UPDATE QRTZ_CALENDARS SET CALENDAR=@blob", saved);
        await Assert.ThrowsAsync<InvalidDataException>(() => BinaryStoreMigration.ConvertAsync(store.Connection, "QRTZ_", "QuartzScheduler", true, TestContext.Current.CancellationToken));
        Assert.Equal(original, await store.Blob("JOB_DETAILS", "JOB_DATA"));
        Assert.Equal(saved, await store.Blob("CALENDARS", "CALENDAR"));
    }

    public sealed class NoopJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    // Quartz 3 writes job and trigger data as a serialized JobDataMap, never as a bare dictionary.
    private static JobDataMap JobData(IDictionary entries)
    {
        var map = new JobDataMap();
        foreach (DictionaryEntry entry in entries) map.Put((string)entry.Key, entry.Value!);
        return map;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"quartz-binary-{Guid.NewGuid():N}.db");
        public SqliteConnection Connection { get; }
        public byte[] Message { get; private set; } = [];
        public object? Schedule { get; private set; }
        private Fixture() => Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());

        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            try
            {
                await fixture.Connection.OpenAsync(TestContext.Current.CancellationToken);
                await fixture.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "quartz3_legacy_job.sql"), TestContext.Current.CancellationToken));
                var json = new JsonObjectSerializer();
                json.Initialize();
                var legacy = json.DeSerialize<IDictionary>(await fixture.Blob("JOB_DETAILS", "JOB_DATA"))!;
                fixture.Message = (byte[])legacy["message"]!;
                var binary = new BinaryObjectSerializer();
                binary.Initialize();
                await fixture.WriteBlob("UPDATE QRTZ_JOB_DETAILS SET JOB_DATA=@blob", binary.Serialize(JobData(legacy)));
                await fixture.WriteBlob("UPDATE QRTZ_TRIGGERS SET JOB_DATA=@blob", binary.Serialize(JobData(new Dictionary<string, object> { ["count"] = 123 })));
                await fixture.WriteBlob("INSERT INTO QRTZ_CALENDARS VALUES ('QuartzScheduler','holiday',@blob)", binary.Serialize(new HolidayCalendar()));
                fixture.Schedule = await fixture.Scalar("SELECT START_TIME || ':' || NEXT_FIRE_TIME || ':' || MISFIRE_INSTR FROM QRTZ_TRIGGERS");
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<byte[]> Blob(string table, string column, string? filter = null) =>
            (byte[])(await Scalar($"SELECT {column} FROM QRTZ_{table}" + (filter is null ? "" : " WHERE " + filter)))!;
        public async Task<object?> Scalar(string sql)
        {
            await using var command = Connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
        public async Task Execute(string sql)
        {
            await using var command = Connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        public async Task WriteBlob(string sql, byte[] bytes)
        {
            await using var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@blob", bytes);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            File.Delete(_path);
        }
    }
}
