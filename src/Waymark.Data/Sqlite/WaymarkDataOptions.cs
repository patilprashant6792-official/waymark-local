using Microsoft.Extensions.Configuration;

namespace Waymark.Data.Sqlite;

public sealed record WaymarkDataOptions
{
    public const string DatabaseFileName = "waymark.db";

    public const string DataDirectoryConfigKey = "Waymark:DataDirectory";

    public WaymarkDataOptions(string databasePath, TimeSpan? busyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathRooted(databasePath))
        {
            throw new ArgumentException("The database path must be absolute.", nameof(databasePath));
        }

        var timeout = busyTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeout), "The busy timeout must be between 0 and 1 minute.");
        }

        DatabasePath = databasePath;
        BusyTimeout = timeout;
    }

    public string DatabasePath { get; }

    /// <summary>How long a connection waits for a lock before SQLite reports the database as busy.</summary>
    public TimeSpan BusyTimeout { get; }

    /// <summary>%LOCALAPPDATA%\Waymark on Windows.</summary>
    public static string DefaultDataDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("The local application data folder could not be determined; set " + DataDirectoryConfigKey + ".");
            }

            return Path.Combine(root, "Waymark");
        }
    }

    public static WaymarkDataOptions ForDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new WaymarkDataOptions(Path.Combine(Path.GetFullPath(directory), DatabaseFileName));
    }

    /// <summary>Uses Waymark:DataDirectory when set, otherwise the default data directory.</summary>
    public static WaymarkDataOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[DataDirectoryConfigKey];
        return ForDirectory(string.IsNullOrWhiteSpace(configured) ? DefaultDataDirectory : configured);
    }
}
