using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Host.Components.Ui;
using Waymark.Host.Memory;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// The Setup page's card for search by meaning: provider, model and dimension, the provider's key (save, clear, last four only), Test, the switch (off until a test passed),
/// the live index status, Re-embed all and Delete stored vectors. Every action goes through <see cref="MemoryAdminService"/>, which audits it.
/// </summary>
public partial class MemorySearchCard : LiveComponentBase
{
    /// <summary>What the switch does, shown under it. The exact wording is part of the privacy promise; docs/safety.md repeats it.</summary>
    public const string NoticeText =
        "When on, Waymark sends the text of your notes and flows (names, summaries, step names, notes; never file contents) to the provider you chose, to make them searchable by meaning. " +
        "Keys are encrypted on this computer. Turn it off any time; search then uses words only.";

    private MemorySettings? _settings;
    private MemoryKeyInfo? _key;
    private MemoryTestResult? _test;
    private MemoryIndexSnapshot _status = new(MemoryIndexState.Off, 0, 0, null, null, null);
    private string _provider = string.Empty;
    private string _model = string.Empty;
    private string _dimension = MemorySettings.DefaultDimension.ToString(CultureInfo.InvariantCulture);
    private bool _enabled;
    private bool _tested;
    private bool _busy;
    private string? _error;

    [Inject]
    private MemoryAdminService Service { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<MemorySearchCard> Logger { get; set; } = default!;

    private IReadOnlyList<WmOption<string>> ProviderOptions => [.. Service.Providers.Select(p => new WmOption<string>(p.Id, p.DisplayName))];

    private string ProviderName => Service.Providers.FirstOrDefault(p => p.Id == _provider)?.DisplayName ?? _provider;

    private string DefaultModel => Service.Providers.FirstOrDefault(p => p.Id == _provider)?.DefaultModel ?? string.Empty;

    private bool ModelChanged =>
        _settings is not null
        && (!string.Equals(_provider, _settings.Provider, StringComparison.Ordinal)
            || !string.Equals(_model.Trim(), _settings.Model, StringComparison.Ordinal)
            || !string.Equals(_dimension.Trim(), _settings.Dimension.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private (StatusState State, string Text) StateChip => _status.State switch
    {
        MemoryIndexState.Working => (StatusState.Working, "Embedding"),
        MemoryIndexState.Idle => (StatusState.Healthy, "Up to date"),
        MemoryIndexState.KeyRejected => (StatusState.Failed, "Key refused"),
        MemoryIndexState.Backoff => (StatusState.Attention, "Waiting to retry"),
        _ => (StatusState.Off, "Off"),
    };

    protected override async Task OnInitializedAsync()
    {
        RefreshOn<MemoryIndexStatusChanged>(() =>
        {
            _status = Service.Status;
            return Task.CompletedTask;
        });
        RefreshOn<MemorySettingsChanged>(LoadAsync);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _settings = await Service.GetAsync(Cancel);
            _provider = _settings.Provider;
            _model = _settings.Model;
            _dimension = _settings.Dimension.ToString(CultureInfo.InvariantCulture);
            _enabled = _settings.Enabled;
            _tested = Service.IsTested(_settings);
            _key = await Service.GetKeyInfoAsync(_provider, Cancel);
            _status = Service.Status;
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The search-by-meaning settings could not be read");
            _error = "The settings could not be read.";
        }
    }

    private async Task ChangeProvider(string provider)
    {
        _provider = provider;
        _model = DefaultModel;
        _test = null;
        _key = await Service.GetKeyInfoAsync(provider, Cancel);
    }

    private Task SaveModelAsync() => RunAsync(async () =>
    {
        if (!int.TryParse(_dimension.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dimension))
        {
            throw new ArgumentException($"The dimension is a whole number from {MemorySettings.MinDimension} to {MemorySettings.MaxDimension}.");
        }

        await Service.SetModelAsync(_provider, string.IsNullOrWhiteSpace(_model) ? DefaultModel : _model.Trim(), dimension, Cancel);
        _test = null;
        Snackbar.Add("Saved. Test the key with this model next.", Severity.Success);
    });

    private Task SaveKeyAsync(string key) => RunAsync(async () =>
    {
        await Service.SaveKeyAsync(_provider, key, Cancel);
        _test = null;
        Snackbar.Add("Key saved. Test it next.", Severity.Success);
    });

    private Task ClearKeyAsync() => RunAsync(async () =>
    {
        await Service.ClearKeyAsync(_provider, Cancel);
        _test = null;
        Snackbar.Add("Key removed.", Severity.Success);
    });

    private Task TestAsync() => RunAsync(async () =>
    {
        _test = await Service.TestAsync(Cancel);
        _tested = _settings is not null && Service.IsTested(_settings);
    });

    private Task SetEnabledAsync(bool enabled) => RunAsync(async () =>
    {
        await Service.SetEnabledAsync(enabled, Cancel);
        _enabled = enabled;
        Snackbar.Add(enabled ? "Search by meaning is on. Notes and flows are embedded in the background." : "Search by meaning is off. Nothing more is sent.", Severity.Success);
    });

    private Task ReembedAsync() => RunAsync(async () =>
    {
        var removed = await Service.ReembedAllAsync(Cancel);
        Snackbar.Add($"{removed} stored vectors dropped; everything is embedded again in the background.", Severity.Success);
    });

    private async Task DeleteVectorsAsync()
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Delete the stored vectors?",
            _enabled
                ? "Every stored vector is deleted. Search by meaning is on, so they are made again: switch it off first to keep none."
                : "Every stored vector is deleted from this computer. Notes and flows themselves are kept.",
            "Delete",
            destructive: true);
        if (confirmed)
        {
            await RunAsync(async () =>
            {
                var removed = await Service.DeleteVectorsAsync(Cancel);
                Snackbar.Add($"{removed} stored vectors deleted.", Severity.Success);
            });
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        _busy = true;
        try
        {
            await work();
            _error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A search-by-meaning setting could not be changed");
            _error = "The change could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }
}
