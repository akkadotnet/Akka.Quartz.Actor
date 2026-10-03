# Quartz 4 upgrade walkthrough

This sample repeats [the upgrade guide](../../docs/upgrading-to-quartz4.md) end to end and produces its screenshots.

- `LegacyApp` is an existing application on **Akka.Quartz.Actor 1.5.59 and Quartz 3.14**. It creates a SQLite store with the Quartz 3 schema and schedules four persistent jobs: a heartbeat every five seconds, a daily report that uses a holiday calendar, an inventory sync with the cron expression `0 0 9 ? * MON/2` (every other Monday in Quartz 3, rejected by Quartz 4.0.1), and a weekly cleanup in a paused trigger group. It can store them with the `binary` serializer (path C in the guide) or Quartz 3's `json` serializer (path B).
- `NewApp` is the same application upgraded to **Akka.Quartz.Actor 1.5.71-beta1 and Quartz 4.0.1**, built from this repository. It reads the migrated store and prints each delivered message.
- `run-walkthrough.sh` seeds a store with `LegacyApp`, copies it to `rehearsal.db`, and then follows the guide: baseline queries, audit, schedule fix, conversion, a second audit, the official Quartz 4.0.1 schema script, and a run of `NewApp`. Each step's commands and output are saved as a transcript.
- `render-screenshots.py` turns the transcripts into the terminal screenshots in `docs/images/upgrade`.

## Run it

You need the .NET 10 SDK and the `sqlite3` command-line shell. From this folder:

```shell
./run-walkthrough.sh out binary      # path C: binary data that must be converted
./run-walkthrough.sh out-json json   # path B: JSON data, no conversion step
```

Transcripts are written to `out/transcripts`. To regenerate the guide's screenshots, you also need Chrome or Chromium:

```shell
python3 render-screenshots.py out/transcripts ../../docs/images/upgrade /path/to/chrome
```

The legacy application runs on .NET 10 with Microsoft's unsupported `System.Runtime.Serialization.Formatters` package so that it can still write BinaryFormatter data. Real Quartz 3 applications usually ran on an older runtime; the stored data is the same.
