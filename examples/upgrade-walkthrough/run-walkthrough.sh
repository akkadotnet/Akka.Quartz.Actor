#!/usr/bin/env bash
# Seeds a Quartz 3 SQLite store with Akka.Quartz.Actor 1.5.59, then upgrades it by following
# docs/upgrading-to-quartz4.md. Each step's commands and output are written to <work>/transcripts.
#
#   ./run-walkthrough.sh [work-directory] [binary|json]
#
# Exits non-zero unless the upgraded store audits clean and the upgraded application receives a saved job.
# Requires the .NET 10 SDK and the sqlite3 command-line shell.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="$(mkdir -p "${1:-$here/out}" && cd "${1:-$here/out}" && pwd)"
serializer="${2:-binary}"
transcripts="$work/transcripts"

rm -rf "$work/legacy-app" "$work/new-app" "$work/quartz3-convert" "$work/quartz4-audit" "$transcripts" "$work"/*.db
mkdir -p "$transcripts"

echo "Building the sample applications and upgrade tools..."
dotnet build -c Release "$here/LegacyApp" -o "$work/legacy-app" -v q -nologo
dotnet build -c Release "$here/NewApp" -o "$work/new-app" -v q -nologo
dotnet publish -c Release "$repo/migration/Quartz3Migration/Quartz3Migration.csproj" -o "$work/quartz3-convert" -v q -nologo
dotnet publish -c Release "$repo/src/Akka.Quartz.Actor.Upgrade/Akka.Quartz.Actor.Upgrade.csproj" -o "$work/quartz4-audit" -v q -nologo
# Copied verbatim from quartznet/quartznet v4.0.1, database/migrations/4.0.
cp "$repo/src/Akka.Quartz.Actor.IntegrationTests/schema_30_to_40_upgrade_sqlite.sql" "$work/"

cd "$work"
current=""
step() { current="$transcripts/$1.txt"; : > "$current"; echo "== $1"; }
# Records the command as typed, then its output. Failing commands are expected in some steps.
run() {
    printf '$ %s\n' "$*" | tee -a "$current"
    set +e
    bash -c "$*" 2>&1 | tee -a "$current"
    local status=${PIPESTATUS[0]}
    set -e
    [ "$status" -eq 0 ] || printf '(exit code %s)\n' "$status" | tee -a "$current"
    echo >> "$current"
}

audit="dotnet quartz4-audit/Akka.Quartz.Actor.Upgrade.dll --provider sqlite"
convert="dotnet quartz3-convert/Quartz3Migration.dll --provider sqlite"
query="sqlite3 -header -column rehearsal.db"
# NEXT_FIRE_TIME is stored as UTC .NET ticks; the queries below also show it as a readable UTC time.

step 01-legacy-app
run "dotnet legacy-app/LegacyApp.dll seed quartz.db $serializer"

step 02-baseline
run "cp quartz.db rehearsal.db"
run "$query \"SELECT DISTINCT SCHED_NAME FROM QRTZ_TRIGGERS;\""
run "$query \"SELECT TRIGGER_GROUP, TRIGGER_NAME, TRIGGER_TYPE, TRIGGER_STATE, datetime((NEXT_FIRE_TIME - 621355968000000000) / 10000000, 'unixepoch') AS NEXT_FIRE_UTC FROM QRTZ_TRIGGERS;\""
run "$query \"SELECT * FROM QRTZ_PAUSED_TRIGGER_GRPS;\""
run "$query \"SELECT TRIGGER_NAME, CRON_EXPRESSION, TIME_ZONE_ID FROM QRTZ_CRON_TRIGGERS;\""

step 03-audit-before
run "$audit --database rehearsal.db --scheduler QuartzScheduler"

step 04-fix-cron
run "dotnet legacy-app/LegacyApp.dll fix-cron rehearsal.db $serializer"

if [ "$serializer" = "binary" ]; then
    step 05-dry-run-mistakes
    run "$convert --database rehearsal.db --scheduler QuartzScheduler"
    run "$convert --database rehearsal.db --scheduler QuartzSchedular --trusted-backup"

    step 05-dry-run
    run "$convert --database rehearsal.db --scheduler QuartzScheduler --trusted-backup"

    step 06-apply
    run "$convert --database rehearsal.db --scheduler QuartzScheduler --trusted-backup --apply"
    run "$convert --database rehearsal.db --scheduler QuartzScheduler --trusted-backup --apply --schedulers-stopped"
    run "$convert --database rehearsal.db --scheduler QuartzScheduler --trusted-backup --apply --schedulers-stopped"
fi

step 07-audit-after
run "$audit --database rehearsal.db --scheduler QuartzScheduler"

step 08-schema
run "sqlite3 rehearsal.db < schema_30_to_40_upgrade_sqlite.sql"
run "$query \"SELECT COUNT(*) AS paused_job_groups FROM QRTZ_PAUSED_JOB_GRPS;\""

step 09-new-app
run "dotnet new-app/NewApp.dll rehearsal.db 12"

step 10-verify
run "$query \"SELECT TRIGGER_GROUP, TRIGGER_NAME, TRIGGER_TYPE, TRIGGER_STATE, datetime((NEXT_FIRE_TIME - 621355968000000000) / 10000000, 'unixepoch') AS NEXT_FIRE_UTC FROM QRTZ_TRIGGERS;\""
run "$query \"SELECT * FROM QRTZ_PAUSED_TRIGGER_GRPS;\""

echo "Transcripts written to $transcripts"

# The walkthrough doubles as an end-to-end check: the store must audit clean and the saved job must fire on Quartz 4.
grep -q '"Issues": \[\]' "$transcripts/07-audit-after.txt" || { echo "FAILED: the audit after the upgrade reported issues." >&2; exit 1; }
grep -q "\[Quartz 4\] .* received 'heartbeat'" "$transcripts/09-new-app.txt" || { echo "FAILED: the upgraded application received no saved job." >&2; exit 1; }
echo "Walkthrough passed: the upgraded application received jobs saved by Akka.Quartz.Actor 1.5.59 ($serializer storage)."
