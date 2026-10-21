using System.Globalization;
using Microsoft.AspNetCore.Components;
using Waymark.Core.Events;
using Waymark.Core.NuGetIndex;
using Waymark.Host.Components.Shared;
using Waymark.Host.NuGetAdmin;

namespace Waymark.Host.Components.Pages;

public partial class NuGetExplorerPage : LiveComponentBase
{
    private const int PageSize = 50;
    private const int MaxLimit = 200;
    private const int MaxNamespacesShown = 300;
    private static readonly string[] KindOptions = [string.Empty, "class", "interface", "enum", "struct", "delegate", "method"];
    private const string Keep = "\u0001keep";

    private NuGetPackageOutline? _outline;
    private NuGetExploreResult? _result;
    private NuGetSymbolDetail? _detail;
    private string? _error;
    private string _nsFilter = string.Empty;
    private string _text = string.Empty;
    private string _kind = string.Empty;
    private bool _loading = true;
    private bool _detailLoading;
    private int _limit = PageSize;
    private string _searchSignature = string.Empty;
    private string _detailSignature = string.Empty;
    private string _packageSignature = string.Empty;

    [Parameter]
    public string PackageId { get; set; } = string.Empty;

    [Parameter]
    public string Version { get; set; } = string.Empty;

    [Parameter]
    public string Framework { get; set; } = string.Empty;

    [SupplyParameterFromQuery(Name = "q")]
    public string? Q { get; set; }

    [SupplyParameterFromQuery(Name = "ns")]
    public string? Ns { get; set; }

    [SupplyParameterFromQuery(Name = "kind")]
    public string? KindFilter { get; set; }

    [SupplyParameterFromQuery(Name = "type")]
    public string? Type { get; set; }

    [SupplyParameterFromQuery(Name = "sid")]
    public string? Sid { get; set; }

    [Inject]
    private NuGetExplorerService Service { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private ILogger<NuGetExplorerPage> Logger { get; set; } = default!;

    private NuGetPackageKey Key => new(PackageId, Version, Framework);

    private bool HasFilters => !string.IsNullOrWhiteSpace(Q) || !string.IsNullOrEmpty(Ns) || !string.IsNullOrEmpty(KindFilter) || !string.IsNullOrEmpty(Type);

    private IEnumerable<NuGetNamespaceCount> VisibleNamespaces =>
        (_outline?.Namespaces ?? [])
            .Where(item => string.IsNullOrWhiteSpace(_nsFilter) || NamespaceLabel(item.Namespace).Contains(_nsFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(MaxNamespacesShown);

    private int HiddenNamespaces =>
        Math.Max(0, (_outline?.Namespaces ?? []).Count(item => string.IsNullOrWhiteSpace(_nsFilter) || NamespaceLabel(item.Namespace).Contains(_nsFilter.Trim(), StringComparison.OrdinalIgnoreCase)) - MaxNamespacesShown);

    protected override void OnInitialized() => RefreshOn<NuGetCacheChanged>(ReloadAllAsync);

    protected override async Task OnParametersSetAsync()
    {
        _text = Q ?? string.Empty;
        _kind = (KindFilter ?? string.Empty).ToLowerInvariant();

        var packageSignature = $"{PackageId}|{Version}|{Framework}";
        if (!string.Equals(packageSignature, _packageSignature, StringComparison.Ordinal))
        {
            _packageSignature = packageSignature;
            _searchSignature = string.Empty;
            _detailSignature = string.Empty;
            _limit = PageSize;
            await LoadOutlineAsync();
        }

        await LoadSearchAsync(force: false);
        await LoadDetailAsync(force: false);
    }

    private async Task ReloadAllAsync()
    {
        await LoadOutlineAsync();
        await LoadSearchAsync(force: true);
        await LoadDetailAsync(force: true);
    }

    private async Task LoadOutlineAsync()
    {
        _loading = true;
        try
        {
            _outline = await Service.GetOutlineAsync(Key, Cancel);
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The NuGet package outline could not be loaded for {PackageId}", PackageId);
            _error = "The package could not be read from the index.";
            _outline = null;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadSearchAsync(bool force)
    {
        if (_outline is not { Package.State: NuGetIndexState.Ready })
        {
            _result = null;
            return;
        }

        var signature = $"{Q}|{Ns}|{_kind}|{Type}|{_limit}";
        if (!force && string.Equals(signature, _searchSignature, StringComparison.Ordinal))
        {
            return;
        }

        _searchSignature = signature;
        try
        {
            var query = new NuGetExploreQuery(Key, Q, ParseKind(_kind), Ns, Type, _limit);
            _result = await Service.SearchAsync(query, Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The NuGet search failed for {PackageId}", PackageId);
            _error = "The search failed. See the log for details.";
            _result = null;
        }
    }

    private async Task LoadDetailAsync(bool force)
    {
        if (_outline is not { Package.State: NuGetIndexState.Ready } || string.IsNullOrWhiteSpace(Sid))
        {
            _detail = null;
            _detailSignature = string.Empty;
            return;
        }

        if (!force && string.Equals(Sid, _detailSignature, StringComparison.Ordinal))
        {
            return;
        }

        _detailSignature = Sid;
        _detailLoading = true;
        try
        {
            _detail = await Service.GetSymbolAsync(Key, Sid, Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The NuGet symbol {Sid} could not be loaded for {PackageId}", Sid, PackageId);
            _error = "The symbol could not be read from the index.";
            _detail = null;
        }
        finally
        {
            _detailLoading = false;
        }
    }

    private void OnSearchKey(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            Search();
        }
    }

    private void OnKindChanged(string kind)
    {
        _kind = kind ?? string.Empty;
        Search();
    }

    private Task ShowMoreAsync()
    {
        _limit = Math.Min(MaxLimit, _limit + PageSize);
        return LoadSearchAsync(force: true);
    }

    // Searching resets the declaring-type filter: it belongs to the type the person was looking at.
    private void Search() => Navigation.NavigateTo(Href(q: _text.Trim(), kind: _kind, type: null));

    // Builds the address of this page with some parts replaced. A part that is not passed keeps its current value; null or empty removes it.
    private string Href(string? q = Keep, string? ns = Keep, string? kind = Keep, string? type = Keep, string? sid = Keep)
    {
        var parts = new List<string>();
        Add(parts, "q", ReferenceEquals(q, Keep) ? Q : q);
        Add(parts, "ns", ReferenceEquals(ns, Keep) ? Ns : ns);
        Add(parts, "kind", ReferenceEquals(kind, Keep) ? KindFilter : kind);
        Add(parts, "type", ReferenceEquals(type, Keep) ? Type : type);
        Add(parts, "sid", ReferenceEquals(sid, Keep) ? Sid : sid);
        var path = NuGetLinks.Explore(Key);
        return parts.Count == 0 ? path : path + "?" + string.Join('&', parts);
    }

    private static void Add(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(name + "=" + Uri.EscapeDataString(value));
        }
    }

    private static string NamespaceLabel(string ns) => ns.Length == 0 ? NuGetExplorerService.GlobalNamespace : ns;

    private static NuGetSymbolKind? ParseKind(string kind) => kind switch
    {
        "class" => NuGetSymbolKind.Class,
        "interface" => NuGetSymbolKind.Interface,
        "enum" => NuGetSymbolKind.Enum,
        "struct" => NuGetSymbolKind.Struct,
        "delegate" => NuGetSymbolKind.Delegate,
        "method" => NuGetSymbolKind.MethodGroup,
        _ => null,
    };

    private static string KindLabel(NuGetSymbolKind kind) => kind == NuGetSymbolKind.MethodGroup ? "method" : kind.ToString().ToLowerInvariant();

    private static bool IsBare(NuGetSymbolDetail detail) =>
        string.IsNullOrWhiteSpace(detail.Documentation.Summary) && detail.Documentation.Parameters.Count == 0 && string.IsNullOrWhiteSpace(detail.Documentation.Returns);

    private string Describe()
    {
        if (_result is null)
        {
            return string.Empty;
        }

        var scope = string.IsNullOrWhiteSpace(Q) && !HasFilters ? "Most used first. " : string.Empty;
        var shown = _result.Items.Count.ToString(CultureInfo.CurrentCulture);
        var total = _result.Total.ToString(CultureInfo.CurrentCulture);
        return $"{scope}Showing {shown} of {total}. ";
    }
}
