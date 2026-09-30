$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot '../runTests.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "quartz-ci-tests-$([Guid]::NewGuid())"

function global:dotnet {
    $global:QuartzCiInvocations++
    $global:LASTEXITCODE = if ($global:QuartzCiCase -eq 'native-failure' -and $global:QuartzCiInvocations -eq 1) { 2 } else { 0 }
    if ($global:QuartzCiCase -eq 'missing-report') { return }
    $directory = $args[[Array]::IndexOf($args, '--results-directory') + 1]
    $executed = if ($global:QuartzCiCase -eq 'zero-tests') { 0 } else { 3 }
    $failed = if ($global:QuartzCiCase -eq 'failed-report') { 1 } else { 0 }
    "<TestRun><ResultSummary><Counters executed='$executed' failed='$failed' /></ResultSummary></TestRun>" |
        Set-Content (Join-Path $directory 'test.trx')
}

try {
    foreach ($case in @('success', 'native-failure', 'failed-report', 'zero-tests', 'missing-report', 'no-projects', 'windows-selection')) {
        $global:QuartzCiCase = $case
        $global:QuartzCiInvocations = 0
        $repository = Join-Path $fixtureRoot $case
        New-Item -ItemType Directory -Path $repository -Force | Out-Null
        if ($case -ne 'no-projects') {
            '<Project />' | Set-Content (Join-Path $repository 'A.Tests.csproj')
            '<Project />' | Set-Content (Join-Path $repository 'B.IntegrationTests.csproj')
            '<Project />' | Set-Content (Join-Path $repository 'C.DatabaseTests.csproj')
        }
        # Stale reports must never make the zero-test/missing-report cases pass.
        $results = Join-Path $repository 'TestResults'
        New-Item -ItemType Directory -Path $results -Force | Out-Null
        '<TestRun><ResultSummary><Counters executed="20" failed="0" /></ResultSummary></TestRun>' |
            Set-Content (Join-Path $results 'stale.trx')
        $failure = $null
        try { & $runner -RepositoryRoot $repository -ResultsDirectory $results -ExcludeDatabaseTests:($case -eq 'windows-selection') }
        catch { $failure = $_ }
        if (($case -in @('success', 'windows-selection')) -ne ($null -eq $failure)) { throw "Unexpected outcome for $case`: $failure" }
        if ($case -eq 'success' -and $global:QuartzCiInvocations -ne 3) { throw 'Integration test discovery failed.' }
        if ($case -eq 'windows-selection' -and $global:QuartzCiInvocations -ne 2) { throw 'Windows selection must exclude only the container project.' }
        if ($case -eq 'native-failure' -and $global:QuartzCiInvocations -ne 1) { throw 'An earlier failure was allowed to be masked by a later project.' }
    }
    Write-Host 'All 7 CI runner scenarios passed (including stale reports and first-project failure).'
}
finally {
    Remove-Item Function:\dotnet
    Remove-Item $fixtureRoot -Recurse -Force
}
