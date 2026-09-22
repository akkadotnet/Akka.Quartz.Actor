$sourceBranch = $env:BUILD_SOURCEBRANCH
if ($sourceBranch -notmatch '^refs/tags/(?<tag>.+)$') {
    throw "Release builds require a tag ref; got '$sourceBranch'."
}

$tag = $Matches.tag
$project = Join-Path $PSScriptRoot '../src/Akka.Quartz.Actor/Akka.Quartz.Actor.csproj'
$packageVersion = & dotnet msbuild $project -getProperty:PackageVersion
if ($LASTEXITCODE -ne 0) {
    throw 'Could not evaluate the package version.'
}

$packageVersion = $packageVersion.Trim()
if ([string]::IsNullOrWhiteSpace($packageVersion) -or $tag -cne $packageVersion) {
    throw "Release tag '$tag' does not match package version '$packageVersion' generated from RELEASE_NOTES.md."
}

Write-Output "Release tag '$tag' matches package version '$packageVersion'."
