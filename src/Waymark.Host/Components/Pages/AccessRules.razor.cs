using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Security;

namespace Waymark.Host.Components.Pages;

public partial class AccessRules : LiveComponentBase
{
    private IReadOnlyList<AccessRule> _rules = [];
    private Workspace? _workspace;
    private AccessListing? _listing;
    private AccessRuleKind _kind = AccessRuleKind.BlockFolder;
    private string _pattern = string.Empty;
    private string _note = string.Empty;
    private string _query = string.Empty;
    private string _searchedFor = string.Empty;
    private string _folder = string.Empty;
    private string? _error;
    private bool _searching;
    private bool _exploring;
    private bool _loaded;
    private bool _busy;

    [Parameter]
    public long WorkspaceId { get; set; }

    [Inject]
    private AccessRuleService Service { get; set; } = default!;

    [Inject]
    private AccessBrowser Browser { get; set; } = default!;

    [Inject]
    private FileAccessPolicy Policy { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<AccessRules> Logger { get; set; } = default!;

    protected override void OnInitialized() => RefreshOn<AccessRulesChanged>(LoadAsync);

    protected override async Task OnParametersSetAsync()
    {
        _folder = string.Empty;
        _searching = false;
        _listing = null;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _workspace = await Service.FindWorkspaceAsync(WorkspaceId, Cancel);
            _rules = _workspace is null ? [] : await Service.ListAsync(WorkspaceId, Cancel);
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The access rules could not be loaded");
            _error = "The access rules could not be loaded.";
            _loaded = true;
            return;
        }

        if (_workspace is not null)
        {
            await RefreshListingAsync();
        }
    }

    // Shows the current folder or the current search again; the verdicts change whenever a rule does.
    private Task RefreshListingAsync() => _searching ? RunSearchAsync(_searchedFor) : RunBrowseAsync(_folder);

    private async Task RunBrowseAsync(string folder)
    {
        _exploring = true;
        try
        {
            _listing = await Browser.BrowseAsync(WorkspaceId, folder, Cancel);
            _folder = folder;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            _error = ex is IOException or UnauthorizedAccessException ? "That folder cannot be listed." : ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The folder could not be listed");
            _error = "The folder could not be listed.";
        }
        finally
        {
            _exploring = false;
        }
    }

    private async Task RunSearchAsync(string query)
    {
        _exploring = true;
        try
        {
            _listing = await Browser.SearchAsync(WorkspaceId, query, Cancel);
            _searchedFor = query;
            _searching = true;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The search failed");
            _error = "The search failed.";
        }
        finally
        {
            _exploring = false;
        }
    }

    private async Task OpenAsync(string folder)
    {
        _error = null;
        _searching = false;
        await RunBrowseAsync(folder);
    }

    private async Task SearchAsync()
    {
        _error = null;
        await RunSearchAsync(_query);
    }

    private async Task ClearSearchAsync()
    {
        _query = string.Empty;
        await OpenAsync(_folder);
    }

    private async Task OnSearchKey(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && !string.IsNullOrWhiteSpace(_query))
        {
            await SearchAsync();
        }
    }

    private IEnumerable<(string Name, string Path)> Crumbs()
    {
        var path = string.Empty;
        foreach (var segment in _folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? segment : path + "/" + segment;
            yield return (segment, path);
        }
    }

    private async Task BlockAsync(AccessEntry entry)
    {
        _error = null;
        _busy = true;
        try
        {
            await Service.AddAsync(WorkspaceId, AccessRuleKind.BlockFolder, entry.RelativePath, string.Empty, Cancel);
            Snackbar.Add($"'{entry.Name}' is blocked.", Severity.Success);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The access rule could not be saved");
            _error = "The rule could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AddAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            await Service.AddAsync(WorkspaceId, _kind, _pattern, _note, Cancel);
            Snackbar.Add("The rule is active.", Severity.Success);
            _pattern = string.Empty;
            _note = string.Empty;
            await LoadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The access rule could not be saved");
            _error = "The rule could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ToggleAsync(AccessRule rule, bool enabled)
    {
        try
        {
            await Service.SetEnabledAsync(rule.Id, enabled, Cancel);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The access rule could not be changed");
            _error = "The rule could not be changed.";
        }
    }

    private async Task RemoveAsync(AccessRule rule)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Remove this rule?",
            $"'{rule.Pattern}' is removed from this workspace. A block rule stops blocking at once.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await Service.RemoveAsync(rule.Id, Cancel);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The access rule could not be removed");
            _error = "The rule could not be removed.";
        }
    }
}

/// <summary>Plain-language names and hints for the rule types.</summary>
internal static class AccessRulesText
{
    public static string Label(AccessRuleKind kind) => kind switch
    {
        AccessRuleKind.BlockFolder => "Block folder or path",
        AccessRuleKind.BlockFileName => "Block file name",
        AccessRuleKind.BlockKeyword => "Block word",
        AccessRuleKind.AllowExtension => "Allow file type",
        _ => kind.ToString(),
    };

    public static string Help(AccessRuleKind kind) => kind switch
    {
        AccessRuleKind.BlockFolder => "A folder name such as generated, or a path from the workspace root such as src/Legacy or src/Old.cs. * and ? work in a single name.",
        AccessRuleKind.BlockFileName => "A file name or pattern such as *.sql or notes-*.txt",
        AccessRuleKind.BlockKeyword => "A word such as internal; any path containing it is blocked",
        AccessRuleKind.AllowExtension => "A file type such as .proto. Built-in blocks still apply.",
        _ => string.Empty,
    };
}
