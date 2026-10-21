using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waymark.Host.Ui;

/// <summary>The cards on the Home dashboard. The ids are stored in the saved layout, so they never change.</summary>
public static class DashboardCards
{
    public const string Server = "server";
    public const string Tunnel = "tunnel";
    public const string CodeIndex = "code-index";
    public const string Tools = "tools";
    public const string Tokens = "tokens";
    public const string LiveLog = "live-log";
    public const string Commands = "commands";
    public const string RecentCalls = "recent-calls";
    public const string NuGet = "nuget";
    public const string Workspaces = "workspaces";
}

/// <summary>Width is in grid columns (of 12), height in grid rows.</summary>
public sealed record DashboardCardInfo(string Id, string Title, int DefaultWidth, int DefaultHeight, int MinWidth, int MinHeight);

public sealed record CardPlacement(string Id, int Width, int Height);

/// <summary>
/// Which cards the dashboard shows, in which order, and how big each one is. Cards flow left to right in a 12 column grid,
/// so a layout is just an ordered list of sizes: it can never overlap, and it stays valid when a card is added later.
/// </summary>
public sealed class DashboardLayout
{
    public const int Columns = 12;
    public const int MaxHeight = 24;

    /// <summary>The global setting that holds the saved layout.</summary>
    public const string SettingKey = "ui.dashboard_layout";

    private const int CurrentVersion = 1;
    private const int MaxJsonChars = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly DashboardCardInfo[] CatalogInOrder =
    [
        new(DashboardCards.Server, "Server", 3, 4, 2, 3),
        new(DashboardCards.Tunnel, "Tunnel", 3, 4, 2, 3),
        new(DashboardCards.CodeIndex, "Code index", 3, 4, 2, 3),
        new(DashboardCards.Tools, "What the AI sees", 3, 4, 2, 3),
        new(DashboardCards.Tokens, "Tokens sent to the AI", 12, 6, 3, 3),
        new(DashboardCards.LiveLog, "Live log", 7, 8, 3, 3),
        new(DashboardCards.Commands, "Commands", 5, 7, 3, 3),
        new(DashboardCards.RecentCalls, "Recent calls", 7, 7, 4, 3),
        new(DashboardCards.NuGet, "NuGet packages", 5, 8, 3, 3),
        new(DashboardCards.Workspaces, "Workspaces", 12, 4, 3, 3)
    ];

    private static readonly Dictionary<string, DashboardCardInfo> ById = CatalogInOrder.ToDictionary(card => card.Id, StringComparer.Ordinal);

    private DashboardLayout(IReadOnlyList<CardPlacement> cards)
    {
        Cards = cards;
    }

    public IReadOnlyList<CardPlacement> Cards { get; }

    public static IReadOnlyList<DashboardCardInfo> Catalog => CatalogInOrder;

    /// <summary>The card with this id. Throws for an id that is not on the dashboard.</summary>
    public static DashboardCardInfo Info(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ById.TryGetValue(id, out var info)
            ? info
            : throw new ArgumentException($"There is no dashboard card '{id}'.", nameof(id));
    }

    /// <summary>
    /// The layout used until the owner saves one. While there is no workspace yet, adding one is the first thing to do,
    /// so that card moves up to just below the status tiles.
    /// </summary>
    public static DashboardLayout Default(bool workspacesFirst)
    {
        var ids = CatalogInOrder.Select(card => card.Id).ToList();
        if (workspacesFirst)
        {
            ids.Remove(DashboardCards.Workspaces);
            ids.Insert(ids.IndexOf(DashboardCards.LiveLog), DashboardCards.Workspaces);
        }

        return new DashboardLayout(ids.Select(id => new CardPlacement(id, ById[id].DefaultWidth, ById[id].DefaultHeight)).ToList());
    }

    /// <summary>
    /// Turns whatever was asked for into a valid layout and never throws: unknown and repeated cards are dropped, sizes are
    /// kept inside each card's limits, and a card that is missing is added at the end with its default size.
    /// </summary>
    public static DashboardLayout Normalize(IEnumerable<CardPlacement>? requested)
    {
        var cards = new List<CardPlacement>(CatalogInOrder.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var placement in requested ?? [])
        {
            if (placement is null || placement.Id is null || !ById.TryGetValue(placement.Id, out var info) || !seen.Add(placement.Id))
            {
                continue;
            }

            cards.Add(new CardPlacement(
                info.Id,
                Math.Clamp(placement.Width, info.MinWidth, Columns),
                Math.Clamp(placement.Height, info.MinHeight, MaxHeight)));
        }

        foreach (var info in CatalogInOrder.Where(card => !seen.Contains(card.Id)))
        {
            cards.Add(new CardPlacement(info.Id, info.DefaultWidth, info.DefaultHeight));
        }

        return new DashboardLayout(cards);
    }

    /// <summary>Reads a saved layout, or the text the page sends when the owner presses Done. False when it is not a layout at all.</summary>
    public static bool TryParse(string? json, out DashboardLayout layout)
    {
        layout = Default(workspacesFirst: false);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonChars)
        {
            return false;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredLayout>(json, JsonOptions);
            if (stored?.Cards is null)
            {
                return false;
            }

            layout = Normalize(stored.Cards.Select(card => new CardPlacement(card.Id ?? string.Empty, card.W, card.H)));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(new StoredLayout
        {
            Version = CurrentVersion,
            Cards = Cards.Select(card => new StoredCard { Id = card.Id, W = card.Width, H = card.Height }).ToList()
        });

    private sealed class StoredLayout
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("cards")]
        public List<StoredCard>? Cards { get; set; }
    }

    private sealed class StoredCard
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("w")]
        public int W { get; set; }

        [JsonPropertyName("h")]
        public int H { get; set; }
    }
}
