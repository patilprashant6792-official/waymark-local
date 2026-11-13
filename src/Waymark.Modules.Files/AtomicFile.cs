namespace Waymark.Modules.Files;

internal static class AtomicFile
{
    /// <summary>
    /// The new content is written next to the target and then moved over it, so a failure never leaves a half-written file.
    /// On Windows an existing target is swapped with ReplaceFile (File.Replace), which keeps its creation time, ACLs, attributes and named streams;
    /// a plain move would give the file the temporary file's identity. If the swap itself is refused, the move is the fallback.
    /// On macOS and Linux the move is the way, and the target's permission bits are copied to the new file first, so an edited script keeps
    /// its execute bit and a private file stays private.
    /// </summary>
    public static async Task ReplaceAsync(string absolutePath, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(absolutePath)!, $".waymark-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(absolutePath) && TrySwap(temp, absolutePath))
                {
                    return;
                }
            }
            else
            {
                KeepUnixMode(absolutePath, temp);
            }

            File.Move(temp, absolutePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void KeepUnixMode(string target, string temp)
    {
        if (!File.Exists(target))
        {
            return;
        }

        // Read first: a target that cannot be read keeps the default mode of a new file rather than failing the write.
        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        File.SetUnixFileMode(temp, mode);
    }

    // ReplaceFile leaves both files as they were when it fails before the swap, so the caller can still fall back to a move.
    private static bool TrySwap(string temp, string target)
    {
        try
        {
            File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
