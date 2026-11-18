# Development

Building, testing, and publishing from source. Back to the [README](../README.md).

## Build from source

### Prerequisites

- .NET SDK version pinned by [`global.json`](../global.json)
- Windows 10/11

### Build and test

```powershell
dotnet restore AppleMusicLyrics.sln
dotnet build AppleMusicLyrics.sln -c Release --no-restore
dotnet test AppleMusicLyrics.sln -c Release --no-build --results-directory artifacts/test-results
```

Build outputs and intermediate files stay under `artifacts/bin/` and `artifacts/obj/`,
separated by project and configuration.

### Run

```powershell
dotnet run --project src/AppleMusicLyrics.App/AppleMusicLyrics.App.csproj
```

### Publish

Run:

```powershell
./build.ps1
```

When the app is closed, the script replaces `artifacts/publish/win-x64/` after a
successful publish, removing stale package files. If the app is running when the
build starts or finishes, it publishes to `artifacts/publish/win-x64-yyyyMMdd-HHmmss-fff/`
instead, leaving the running app and its files intact. The final `Latest build:`
message gives the executable path. Each build while the app is running gets a new
directory; existing timestamped builds are kept.

All builds use a temporary publish directory first. Apart from this running-app
exception, do not create local folders or ZIPs named after a task, date, or version.
GitHub Release attachments remain versioned.

## Project structure

```text
src/
  AppleMusicLyrics.App/                    WPF desktop application
  AppleMusicLyrics.Application/            Runtime orchestration and use cases
  AppleMusicLyrics.Core/                   Core models and pure logic
  AppleMusicLyrics.Debugger/               Overnight diagnostic monitor
  AppleMusicLyrics.Infrastructure.Windows/ Windows integrations
tests/
  AppleMusicLyrics.Tests/                  Unit tests
```
