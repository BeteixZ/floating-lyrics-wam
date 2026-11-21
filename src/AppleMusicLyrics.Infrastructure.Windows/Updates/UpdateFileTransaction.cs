namespace AppleMusicLyrics.Infrastructure.Windows.Updates;

/// <summary>Replaces package files individually and retains their originals until the restart succeeds.</summary>
internal sealed class UpdateFileTransaction(string target, string backupDirectory)
{
    private readonly Stack<(string Destination, string? Original)> _applied = new();
    private readonly Stack<string> _createdDirectories = new();

    public void Replace(string source, string relativePath)
    {
        var destination = Path.Combine(target, relativePath);
        EnsureDirectory(Path.GetDirectoryName(destination)!);

        string? original = null;
        if (File.Exists(destination))
        {
            if (File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"The package path is a link: {destination}.");
            original = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(destination, original);
        }

        ReplaceFromFile(source, destination);
        _applied.Push((destination, original));
    }

    public bool RollBack(Action<string> log)
    {
        var restored = true;
        while (_applied.TryPop(out var file))
        {
            try
            {
                if (file.Original is { } original) ReplaceFromFile(original, file.Destination);
                else RetryFileOperation(() => File.Delete(file.Destination));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"Could not restore {file.Destination}: {error.Message}");
                restored = false;
            }
        }

        while (_createdDirectories.TryPop(out var directory))
        {
            try { Directory.Delete(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return restored;
    }

    private void EnsureDirectory(string directory)
    {
        if (directory.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
        EnsureDirectory(Path.GetDirectoryName(directory)!);
        if (Directory.Exists(directory))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"The package path points outside the installation: {directory}.");
            return;
        }

        Directory.CreateDirectory(directory);
        _createdDirectories.Push(directory);
    }

    private static void ReplaceFromFile(string source, string destination)
    {
        // Stage beside the destination so the final rename is on the same volume. A failed
        // copy or rename then leaves the original destination untouched.
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".aml-update-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary);
            RetryFileOperation(() => File.Move(temporary, destination, overwrite: true));
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RetryFileOperation(Action operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (IOException error) when (attempt < 12 && (error.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(250);
            }
        }
    }
}
