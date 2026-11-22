# Compares the digest GitHub recorded for an uploaded release asset with the SHA-256 of the file
# CI built. The in-app updater, including the 0.10 client that installs 1.0, trusts that digest,
# so a release stays a draft unless they match.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [string] $ReleaseId,
    [Parameter(Mandatory)] [string] $ZipPath
)

$ErrorActionPreference = 'Stop'
$name = Split-Path -Leaf $ZipPath
$expected = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $ZipPath).Length

$assets = gh api "repos/$Repository/releases/$ReleaseId/assets" --paginate | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "Could not read the assets of release $ReleaseId." }
$matching = @($assets | Where-Object { $_.name -ceq $name })
if ($matching.Count -ne 1) { throw "Expected exactly one asset named $name, found $($matching.Count)." }
$asset = $matching[0]

if ($asset.size -ne $size) { throw "Uploaded size $($asset.size) differs from the built $size bytes." }
if ($asset.digest -ne "sha256:$expected") {
    throw "Uploaded digest '$($asset.digest)' differs from the built sha256:$expected."
}
Write-Host "$name on GitHub matches the built file (sha256:$expected)."
