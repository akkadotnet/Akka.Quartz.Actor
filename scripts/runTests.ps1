param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$ResultsDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "TestResults/$([Guid]::NewGuid())"),
    [switch]$ExcludeDatabaseTests
)

$ErrorActionPreference = 'Stop'
$projects = @(Get-ChildItem -Path $RepositoryRoot -Recurse -File -Filter '*Tests.csproj' |
    Where-Object { $_.FullName -notmatch '[/\\](examples|benchmarks|obj|bin)[/\\]' } |
    Where-Object { -not $ExcludeDatabaseTests -or $_.Name -notlike '*.DatabaseTests.csproj' } |
    Sort-Object FullName)
if ($ExcludeDatabaseTests) { Write-Host 'Container database tests excluded on this host; Linux CI runs them with Docker.' }
if ($projects.Count -eq 0) { throw 'No test projects found.' }

foreach ($project in $projects) {
    # A unique directory means stale reports cannot satisfy this invocation's checks.
    $projectResults = Join-Path $ResultsDirectory "$($project.BaseName)-$([Guid]::NewGuid())"
    New-Item -ItemType Directory -Path $projectResults -Force | Out-Null
    & dotnet test -c Release --no-build --project $project.FullName --results-directory $projectResults -- --report-trx
    if ($LASTEXITCODE -ne 0) { throw "Test project $($project.Name) exited with code $LASTEXITCODE." }
    $reports = @(Get-ChildItem $projectResults -Recurse -File -Filter '*.trx')
    if ($reports.Count -eq 0) { throw "No TRX results for $($project.Name)." }
    $executed = 0
    foreach ($report in $reports) {
        [xml]$result = Get-Content $report.FullName -Raw
        $counters = $result.TestRun.ResultSummary.Counters
        if ($null -eq $counters) { throw "Missing result counters in $($report.Name)." }
        if ([int]$counters.failed -gt 0) { throw "Failed tests reported by $($project.Name)." }
        $executed += [int]$counters.executed
    }
    if ($executed -eq 0) { throw "Zero tests executed by $($project.Name)." }
    Write-Host "$($project.Name): $executed tests executed."
}
