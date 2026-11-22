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

### Pre-push formatting check

CI fails the build (and skips the GitHub Release) when `dotnet format` finds
changes. A versioned `pre-push` hook runs the same check locally; enable it once
per clone:

```powershell
git config core.hooksPath .githooks
```

To fix a failure, run `dotnet format AppleMusicLyrics.sln --severity warn` and commit.

### Release checklist

1. Set `Version`, `InformationalVersion`, `AssemblyVersion` and `FileVersion` in
   [`Directory.Build.props`](../Directory.Build.props) to the new version
   (`AssemblyVersion`/`FileVersion` without a pre-release suffix, e.g. `1.0.0.0`).
2. Add a `## vX.Y.Z` section to `RELEASE_NOTES.md`; it becomes the release body and the
   text shown by the in-app updater.
3. Verify formatting, tests and the tag:

   ```powershell
   dotnet format AppleMusicLyrics.sln --verify-no-changes --severity warn
   dotnet test AppleMusicLyrics.sln -c Release
   ./.github/scripts/Test-ReleaseVersion.ps1 -Tag vX.Y.Z
   ```

4. Commit, tag `vX.Y.Z`, and push the commit and the tag. The pre-push hook
   re-checks formatting; the tag push triggers the release workflow.

The workflow refuses a tag whose version or release notes do not match, checks the
final ZIP (required files and licenses, and that the extracted app starts), uploads it
to a **draft** release, compares the digest GitHub recorded with the built file, and
only then publishes the release. The in-app updater installs only
`AppleMusicLyrics-vX.Y.Z-win-x64.zip` with a SHA-256 digest; anything else is offered
as a download page.

A tag with a suffix, such as `v1.0.0-rc.1`, is published as a GitHub pre-release.
The in-app updater asks only for `/releases/latest`, which never returns a pre-release,
and stable builds would not offer one anyway. Release candidates are therefore installed
by hand from the release page; a candidate is updated to the final version once that is
published. Run a candidate for at least a day of normal use before tagging the final
version.

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
