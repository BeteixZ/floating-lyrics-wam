# Reuse the publish directory unless the app is running; then publish beside it.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'publish'
$publishPath = Join-Path $publishRoot 'win-x64'
$stagingPath = Join-Path $publishRoot '.win-x64-staging'
$previousPath = Join-Path $publishRoot '.win-x64-previous'

function Assert-OutputPath([string] $Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path is outside artifacts: $fullPath"
    }
    $cursor = $fullPath
    while ($cursor -ne $repoRoot) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "Output path contains a link: $cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $fullPath) {
        if (Get-ChildItem -LiteralPath $fullPath -Recurse -Force -Attributes ReparsePoint) {
            throw "Output directory contains a link: $fullPath"
        }
    }
}

foreach ($path in @($publishPath, $stagingPath, $previousPath)) {
    Assert-OutputPath $path
}
function Test-AppRunning {
    if (@(Get-Process -Name 'AppleMusicLyrics.App' -ErrorAction SilentlyContinue).Count -gt 0) {
        return $true
    }
    # Restricted shells may not see the process, but can still detect its file lock.
    if (Test-Path -LiteralPath $publishRoot) {
        foreach ($directory in Get-ChildItem -LiteralPath $publishRoot -Directory -Filter 'win-x64*') {
            $executable = Join-Path $directory.FullName 'AppleMusicLyrics.App.exe'
            if (-not (Test-Path -LiteralPath $executable)) {
                continue
            }
            try {
                $stream = [System.IO.File]::Open($executable, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
                $stream.Dispose()
            }
            catch [System.IO.IOException] {
                if (($_.Exception.HResult -band 0xFFFF) -in @(32, 33)) {
                    return $true
                }
                throw
            }
        }
    }
    return $false
}
$appWasRunning = Test-AppRunning
if (Test-Path -LiteralPath $previousPath) {
    throw "A previous publish needs recovery before building: $previousPath"
}
if (Test-Path -LiteralPath $stagingPath) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
}

Push-Location $repoRoot
try {
    dotnet publish src/AppleMusicLyrics.App/AppleMusicLyrics.App.csproj `
        -c Release -r win-x64 -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        --disable-build-servers -m:1 -o $stagingPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE. The current publish was kept."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stagingPath 'AppleMusicLyrics.App.exe'))) {
        throw 'Publish did not produce AppleMusicLyrics.App.exe.'
    }

    # Sandboxed workspaces can inherit a Low mandatory label onto generated executables.
    # Windows then starts the image at Low even from Explorer, preventing tray registration.
    # Restore only this trusted publish image to ordinary desktop (Medium) integrity.
    # If the build itself is confined, fail before replacing the existing publish.
    Assert-OutputPath $stagingPath
    & icacls.exe (Join-Path $stagingPath 'AppleMusicLyrics.App.exe') /setintegritylevel M | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot restore Medium integrity on the published executable. Run ./build.ps1 from a normal Windows PowerShell, outside the agent sandbox. The current publish was kept.'
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'RELEASE_NOTES.md') `
        -Destination $stagingPath

    # Keep legacy portable settings/cache files if this installation still has them.
    if (Test-Path -LiteralPath $publishPath) {
        Get-ChildItem -LiteralPath $publishPath -File |
            Where-Object { $_.Name -eq 'settings.ini' -or $_.Name -like '*-cache.json' } |
            Copy-Item -Destination $stagingPath
    }

    # Check again in case the app was opened while dotnet publish was running.
    $useTimestamp = $appWasRunning -or (Test-AppRunning)
    if ($useTimestamp) {
        $timestampPath = Join-Path $publishRoot ("win-x64-" + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
        $publishPath = $timestampPath
        $suffix = 1
        while (Test-Path -LiteralPath $publishPath) {
            $publishPath = "$timestampPath-$suffix"
            $suffix++
        }
        Assert-OutputPath $publishPath
        Write-Host 'The app is running; publishing to a new timestamped directory.'
    }
    elseif (Test-Path -LiteralPath $publishPath) {
        Move-Item -LiteralPath $publishPath -Destination $previousPath
    }
    try {
        Move-Item -LiteralPath $stagingPath -Destination $publishPath
    }
    catch {
        if (-not $useTimestamp -and (Test-Path -LiteralPath $previousPath)) {
            Move-Item -LiteralPath $previousPath -Destination $publishPath
        }
        throw
    }
    if (-not $useTimestamp -and (Test-Path -LiteralPath $previousPath)) {
        Remove-Item -LiteralPath $previousPath -Recurse -Force
    }
    Write-Host "Latest build: $(Join-Path $publishPath 'AppleMusicLyrics.App.exe')"
}
finally {
    Pop-Location
}
