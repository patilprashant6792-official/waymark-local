namespace Waymark.Logging;

public sealed class LiveLogOptions
{
    /// <summary>How many entries are kept in memory (100 to 50,000). The oldest are dropped first.</summary>
    public int Capacity { get; set; } = 200;

    public int MaxFields { get; set; } = 24;

    /// <summary>A longer value is cut and says how much was left out.</summary>
    public int MaxValueLength { get; set; } = 200;

    public int MaxMessageLength { get; set; } = 700;

    public int MaxExceptionLength { get; set; } = 2000;
}
