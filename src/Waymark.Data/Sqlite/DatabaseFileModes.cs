using System.Runtime.Versioning;

namespace Waymark.Data.Sqlite;

/// <summary>
/// Makes the database and its -wal and -shm files readable and writable by this user alone (rw-------) on macOS and Linux.
/// SQLite gives a new -wal or -shm file the mode of the database file, so they stay owner-only once the database file is.
/// </summary>
public static class DatabaseFileModes
{
    public const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The database files that exist now, the database first.</summary>
    public static IReadOnlyList<string> FilesOf(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }.Where(File.Exists).ToList();
    }

    /// <summary>Sets the file to rw------- when it is not already. Throws IOException or UnauthorizedAccessException.</summary>
    [UnsupportedOSPlatform("windows")]
    public static void MakeOwnerOnly(string path)
    {
        if (File.GetUnixFileMode(path) != OwnerOnly)
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
    }
}
