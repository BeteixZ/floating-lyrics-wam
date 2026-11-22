# Checks the final release ZIP rather than the build folder: required files and licenses are
# present, nothing that must not ship is included, and the extracted app starts, loads its
# window and exits cleanly without logging a fatal error.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ZipPath,
    [int] $TimeoutSeconds = 60,
    [switch] $SkipLaunch
)

$ErrorActionPreference = 'Stop'
$zip = (Resolve-Path -LiteralPath $ZipPath).Path
$extract = Join-Path ([System.IO.Path]::GetTempPath()) ("aml-package-" + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $zip -DestinationPath $extract

try {
    $required = @(
        'AppleMusicLyrics.App.exe',
        'LICENSE',
        'README.md',
        'RELEASE_NOTES.md',
        'RECOVERY.txt',
        'THIRD_PARTY_NOTICES.md',
        'Fonts/Marcellus-Regular.ttf',
        'licenses/Marcellus-OFL.txt',
        'licenses/MPL-2.0.txt',
        'licenses/Lucene.NET-LICENSE.txt',
        'Languages/en-US.json',
        'Languages/zh-CN.json',
        'Languages/zh-TW.json'
    )
    $problems = @()
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $extract $file))) { $problems += "Missing $file" }
    }
    foreach ($folder in 'licenses/LyricsKit-port/QQMusic', 'licenses/LyricsKit-port/NetEase') {
        if (-not (Get-ChildItem -LiteralPath (Join-Path $extract $folder) -Filter '*.cs' -ErrorAction SilentlyContinue)) {
            $problems += "Missing MPL source files in $folder"
        }
    }
    # Not redistributable, or local state that must never ship.
    $forbidden = Get-ChildItem -LiteralPath $extract -Recurse -File |
        Where-Object { $_.Name -match '^(?i:optima.*\.tt[fc]|settings\.ini|.*-cache\.json|errors(\.\d+)?\.log)$' }
    foreach ($file in $forbidden) { $problems += "Must not ship $($file.FullName.Substring($extract.Length + 1))" }

    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "::error::$_" }
        throw "The release package is incomplete."
    }
    Write-Host 'Package contents are complete.'

    if ($SkipLaunch) { return }

    $logFile = Join-Path $env:LOCALAPPDATA 'AppleMusicLyrics\logs\errors.log'
    $logBefore = if (Test-Path -LiteralPath $logFile) { (Get-Item -LiteralPath $logFile).Length } else { 0 }
    # --smoke-test exits with 0 only after the main window loaded, lyrics polling started and
    # the UI thread answered; a crash exits non-zero and a hang never exits.
    $process = Start-Process -FilePath (Join-Path $extract 'AppleMusicLyrics.App.exe') `
        -ArgumentList '--smoke-test' -PassThru
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "The app did not finish its smoke test within $TimeoutSeconds s (hung during startup?)."
        }
        if ($process.ExitCode -ne 0) {
            throw "The app's smoke test failed with exit code $($process.ExitCode)."
        }
        Write-Host 'The app started, loaded its window and exited cleanly.'
    }
    finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }

    if (Test-Path -LiteralPath $logFile) {
        $stream = [System.IO.File]::Open($logFile, 'Open', 'Read', 'ReadWrite')
        try {
            $null = $stream.Seek($logBefore, 'Begin')
            $newEntries = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
        }
        finally { $stream.Dispose() }
        if ($newEntries) { Write-Host "Errors logged during the smoke test:`n$newEntries" }
        if ($newEntries -match ' FATAL ') { throw 'The app logged a fatal error during the smoke test.' }
    }
}
finally {
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
}
