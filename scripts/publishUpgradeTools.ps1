param([string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/upgrade-tools'))
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
foreach ($tool in @(
    @{ Project = 'migration/Quartz3Migration/Quartz3Migration.csproj'; Directory = 'quartz3-convert' },
    @{ Project = 'src/Akka.Quartz.Actor.Upgrade/Akka.Quartz.Actor.Upgrade.csproj'; Directory = 'quartz4-audit' }
)) {
    & dotnet publish (Join-Path $repository $tool.Project) -c Release -o (Join-Path $OutputDirectory $tool.Directory)
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish $($tool.Project)." }
}
Copy-Item (Join-Path $repository 'docs/upgrading-to-quartz4.md') (Join-Path $OutputDirectory 'UPGRADE_GUIDE.md')
Write-Host "Upgrade tools published to $OutputDirectory. The converter requires .NET 8; the auditor requires .NET 10."
