using MudBlazor;

namespace Waymark.Host.Components.Layout;

public enum NavGroup
{
    Live,
    Configure,
    Setup
}

/// <summary>Which count, if any, the menu shows beside an entry.</summary>
public enum NavBadge
{
    None,
    Approvals,
    Commands
}

public sealed record NavEntry(string Title, string Href, string Icon, NavGroup Group, string Hint, string Keywords, NavBadge Badge = NavBadge.None, bool Exact = false, bool Hidden = false);

/// <summary>
/// The one list of pages. The menu and the Ctrl K picker read it, so a page is renamed, added or hidden in exactly one
/// place. All icons are from the outlined set.
/// </summary>
public static class NavCatalog
{
    public static IReadOnlyList<NavEntry> All { get; } =
    [
        new("Dashboard", string.Empty, Icons.Material.Outlined.Dashboard, NavGroup.Live, "Overview of this machine", "home overview start", Exact: true),
        new("Approvals", "approvals", Icons.Material.Outlined.HowToReg, NavGroup.Live, "Apps waiting to connect", "pairing connect clients", NavBadge.Approvals),
        new("Console", "console", Icons.Material.Outlined.Computer, NavGroup.Live, "Commands Claude runs, live", "terminal output shell run", NavBadge.Commands),
        new("Audit", "audit", Icons.Material.Outlined.History, NavGroup.Live, "Every call, stored, plus the live log", "history log trail live activity watcher auth errors"),
        new("Workspaces", "workspaces", Icons.Material.Outlined.Folder, NavGroup.Configure, "Folders Claude may read", "folders index access rules flows"),
        new("Tools", "tools", Icons.Material.Outlined.Build, NavGroup.Configure, "Switch tools on or off", "profiles token"),
        new("Host prompt", "guides", Icons.Material.Outlined.MenuBook, NavGroup.Configure, "Your instructions for every connected agent", "guides instructions skills teach prompts"),
        new("Packages", "packages", Icons.Material.Outlined.Inventory2, NavGroup.Configure, "Package index", "nuget pypi npm packages explorer cache"),
        new("Commands", "commands", Icons.Material.Outlined.Tune, NavGroup.Configure, "What Claude may run", "gates off ask auto shell trust"),
        // Hidden until we decide what they do; the pages and routes stay.
        new("Connectors", "connectors", Icons.Material.Outlined.Extension, NavGroup.Configure, "Connected apps", "apps data", Hidden: true),
        new("Vault", "vault", Icons.Material.Outlined.Lock, NavGroup.Configure, "Named secrets", "secrets passwords", Hidden: true),
        new("Safety", "safety", Icons.Material.Outlined.Shield, NavGroup.Configure, "Risks, safeguards, exposure ledger", "risk danger leak redact secrets exposure"),
        new("Access keys", "access-keys", Icons.Material.Outlined.VpnKey, NavGroup.Configure, "Keys for clients", "api tokens"),
        new("Setup", "setup", Icons.Material.Outlined.Settings, NavGroup.Setup, "Tunnel, public address and local MCP address", "ngrok tunnel domain mcp local localhost url connector claude code cursor codex")
    ];

    public static IEnumerable<NavEntry> Visible => All.Where(entry => !entry.Hidden);

    public static string GroupLabel(NavGroup group) => group switch
    {
        NavGroup.Live => "Live",
        NavGroup.Configure => "Configure",
        _ => "Setup"
    };
}
