# Fails a release build whose tag, built version and release notes disagree. Without it a tag
# such as v1.0.0 could ship a package that reports 0.10.0 and keeps offering itself as an update.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..\..')
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($RepositoryRoot)

if ($Tag -notmatch '^v(?<core>\d+\.\d+\.\d+)(?<pre>-[0-9A-Za-z.-]+)?$') {
    throw "Release tag '$Tag' is not vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-PRERELEASE."
}
$version = $Tag.Substring(1)
$core = $Matches.core

[xml] $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$group = $props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
$expected = @{
    Version              = $version
    InformationalVersion = $version
    AssemblyVersion      = "$core.0"
    FileVersion          = "$core.0"
}
$problems = @()
foreach ($name in $expected.Keys) {
    $actual = [string] $group.$name
    if ($actual -ne $expected[$name]) {
        $problems += "Directory.Build.props $name is '$actual', expected '$($expected[$name])'."
    }
}

# The release body and the in-app update window show this section.
$notes = Get-Content -LiteralPath (Join-Path $root 'RELEASE_NOTES.md')
$heading = $notes | Where-Object { $_ -eq "## $Tag" -or $_.StartsWith("## $Tag ") } | Select-Object -First 1
if (-not $heading) {
    $problems += "RELEASE_NOTES.md has no '## $Tag' section."
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    throw "Release $Tag is inconsistent."
}
Write-Host "Release $Tag matches the built version and has release notes."
