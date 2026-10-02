param([string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/upgrade-tools'))
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
foreach ($tool in @(
    @{ Project = 'migration/Quartz3Migration/Quartz3Migration.csproj'; Directory = 'quartz3-convert' },
    @{ Project = 'src/Akka.Quartz.Actor.Upgrade/Akka.Quartz.Actor.Upgrade.csproj'; Directory = 'quartz4-audit' }
)) {
    $toolDirectory = Join-Path $OutputDirectory $tool.Directory
    & dotnet publish (Join-Path $repository $tool.Project) -c Release -o $toolDirectory
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish $($tool.Project)." }
    # The archive is framework-dependent and launched with `dotnet <tool>.dll`, as the guide documents.
    $entryPoint = Join-Path $toolDirectory "$([IO.Path]::GetFileNameWithoutExtension($tool.Project)).dll"
    & dotnet $entryPoint --help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Published $($tool.Directory) failed its --help smoke test." }
}
Copy-Item (Join-Path $repository 'docs/upgrading-to-quartz4.md') (Join-Path $OutputDirectory 'UPGRADE_GUIDE.md')
Write-Host "Upgrade tools published to $OutputDirectory. The converter requires .NET 8; the auditor requires .NET 10."
