namespace Waymark.Modules.Files;

internal static class AtomicFile
{
    /// <summary>The new content is written next to the target and then moved over it, so a failure never leaves a half-written file.</summary>
    public static async Task ReplaceAsync(string absolutePath, byte[] bytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(absolutePath)!, $".waymark-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
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
}
