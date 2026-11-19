using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using AppleMusicLyrics.Core.Updates;

namespace AppleMusicLyrics.Infrastructure.Windows.Updates;

public enum UpdateFailure
{
    NoPackage,
    Network,
    Verification,
    InvalidPackage,
    NotWritable,
}

public sealed class UpdateException(UpdateFailure failure, Exception? inner = null)
    : Exception($"Update failed: {failure}", inner)
{
    public UpdateFailure Failure { get; } = failure;
}

/// <summary>
/// Downloads, verifies, and installs a release package for the portable (unzipped) app.
/// <para>
/// Installing works in two processes. The running app stages the new version in
/// <c>%LOCALAPPDATA%\AppleMusicLyrics\updates\staging-&lt;version&gt;</c>, copies its OWN
/// executable to a helper folder, starts that copy with <see cref="ApplyArgument"/>, and exits.
/// The helper waits for the old process to end, copies the staged folder over the install folder,
/// starts the updated app, and exits. Running the installer from the current version (not from
/// the package being installed) means any release can be installed, including one whose own
/// installer is missing or broken. Files are only added or overwritten, never deleted, and
/// settings live outside the install folder, so nothing the user keeps there is lost.
/// </para>
/// </summary>
public sealed class UpdateInstaller(string updatesRoot)
{
    public const string ExecutableName = "AppleMusicLyrics.App.exe";
    public const string ApplyArgument = "--apply-update";
    public const string UpdatedArgument = "--updated";
    private const string StagingPrefix = "staging-";
    private const string HelperPrefix = "helper-";

    public string UpdatesRoot { get; } = updatesRoot;

    /// <summary>
    /// Downloads <paramref name="release"/>'s package, checks its size and SHA-256 digest, and
    /// extracts it to a fresh staging folder. Returns that folder.
    /// </summary>
    public async Task<string> DownloadAndStageAsync(HttpClient httpClient, ReleaseInfo release,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var asset = release.Package ?? throw new UpdateException(UpdateFailure.NoPackage);
        Directory.CreateDirectory(UpdatesRoot);
        var name = SafeName(release.Version.ToString());
        var download = Path.Combine(UpdatesRoot, $"{name}.zip.part");
        var staging = Path.Combine(UpdatesRoot, StagingPrefix + name);

        try
        {
            await DownloadVerifiedAsync(httpClient, asset, download, progress, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            // ExtractToDirectory rejects entries that would land outside the destination.
            ZipFile.ExtractToDirectory(download, staging);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            throw new UpdateException(UpdateFailure.Network, error);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateFailure.InvalidPackage, error);
        }
        finally
        {
            TryDelete(download);
        }

        if (!File.Exists(Path.Combine(staging, ExecutableName)))
        {
            TryDeleteDirectory(staging);
            throw new UpdateException(UpdateFailure.InvalidPackage);
        }

        return staging;
    }

    private static async Task DownloadVerifiedAsync(HttpClient httpClient, ReleaseAsset asset, string path,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var expected = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength ?? 0;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (expected > 0) progress?.Report(Math.Min(1.0, received / (double)expected));
            }
        }

        if (asset.Size > 0 && received != asset.Size) throw new UpdateException(UpdateFailure.Verification);
        if (asset.Sha256 is { } digest
            && !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), digest, StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateFailure.Verification);
        }
    }

    /// <summary>True when the app can replace its own files in <paramref name="directory"/>.</summary>
    public static bool CanWriteTo(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the running executable (<paramref name="currentExecutable"/>) to a fresh helper
    /// folder outside the install folder, so it can replace the install folder's files. Returns
    /// the helper's path.
    /// </summary>
    public string PrepareHelper(string currentExecutable)
    {
        var directory = Path.Combine(UpdatesRoot, HelperPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var helper = Path.Combine(directory, ExecutableName);
        File.Copy(currentExecutable, helper, overwrite: true);
        return helper;
    }

    /// <summary>
    /// Start information for <paramref name="helperExecutable"/> to copy
    /// <paramref name="stagingDirectory"/> into <paramref name="installDirectory"/> once
    /// <paramref name="processId"/> has exited.
    /// </summary>
    public static ProcessStartInfo CreateApplyStartInfo(string helperExecutable, string stagingDirectory,
        string installDirectory, int processId)
    {
        var start = new ProcessStartInfo(helperExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(helperExecutable)!,
        };
        start.ArgumentList.Add(ApplyArgument);
        start.ArgumentList.Add("--source");
        start.ArgumentList.Add(stagingDirectory);
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(installDirectory);
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }

    /// <summary>Parses the arguments <see cref="CreateApplyStartInfo"/> produces.</summary>
    public static bool TryParseApplyArguments(IReadOnlyList<string> args, out string sourceDirectory,
        out string targetDirectory, out int processId)
    {
        sourceDirectory = string.Empty;
        targetDirectory = string.Empty;
        processId = 0;
        if (args.Count == 0 || args[0] != ApplyArgument) return false;
        for (var index = 1; index + 1 < args.Count; index += 2)
        {
            if (args[index] == "--source") sourceDirectory = args[index + 1];
            else if (args[index] == "--target") targetDirectory = args[index + 1];
            else if (args[index] == "--pid") _ = int.TryParse(args[index + 1], out processId);
        }
        return sourceDirectory.Length > 0 && targetDirectory.Length > 0;
    }

    /// <summary>
    /// Runs inside the helper: waits for the old app to exit, then copies
    /// <paramref name="sourceDirectory"/> over <paramref name="targetDirectory"/>. Both folders must
    /// contain the app, so a tampered argument cannot spray files into another folder.
    /// </summary>
    public static bool Apply(string sourceDirectory, string targetDirectory, int processId, TimeSpan waitTimeout,
        Action<string> log)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetDirectory);
        if (!File.Exists(Path.Combine(source, ExecutableName)))
        {
            log($"Refusing to install: {source} does not contain {ExecutableName}.");
            return false;
        }
        if (!File.Exists(Path.Combine(target, ExecutableName)))
        {
            log($"Refusing to install: {target} does not contain {ExecutableName}.");
            return false;
        }
        if (string.Equals(source.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            log("Source and target are the same folder.");
            return false;
        }

        WaitForExit(processId, waitTimeout, log);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!CopyWithRetry(file, destination, log)) return false;
        }

        log($"Installed {source} into {target}.");
        return true;
    }

    /// <summary>Removes staging and helper folders and partial downloads, except the folder the current process runs from.</summary>
    public void CleanUp(string? keepDirectory = null)
    {
        if (!Directory.Exists(UpdatesRoot)) return;
        var keep = keepDirectory is null ? null : Path.GetFullPath(keepDirectory).TrimEnd('\\');
        foreach (var directory in Directory.EnumerateDirectories(UpdatesRoot, StagingPrefix + "*")
            .Concat(Directory.EnumerateDirectories(UpdatesRoot, HelperPrefix + "*")))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), keep, StringComparison.OrdinalIgnoreCase))
                continue;
            TryDeleteDirectory(directory);
        }
        foreach (var part in Directory.EnumerateFiles(UpdatesRoot, "*.zip.part")) TryDelete(part);
    }

    private static void WaitForExit(int processId, TimeSpan timeout, Action<string> log)
    {
        if (processId <= 0) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(timeout)) log($"Process {processId} still running after {timeout.TotalSeconds:0}s; copying anyway.");
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
    }

    private static bool CopyWithRetry(string source, string destination, Action<string> log)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return true;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 40)
            {
                // The old process can hold files briefly after it reports exit (antivirus, handles).
                Thread.Sleep(250);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"Could not replace {destination}: {error.GetType().Name}.");
                return false;
            }
        }
    }

    private static string SafeName(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
