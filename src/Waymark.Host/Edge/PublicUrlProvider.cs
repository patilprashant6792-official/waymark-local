using System.Text.Json;
using Waymark.Core.Edge;
using Waymark.Core.Settings;

namespace Waymark.Host.Edge;

public sealed class PublicUrlProvider(IConfigStore config, ILogger<PublicUrlProvider> logger) : IPublicUrlProvider
{
    private volatile string? _current;

    public string? Current => _current;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.PublicUrl, ct);
        if (setting is null)
        {
            _current = null;
            return;
        }

        try
        {
            _current = JsonSerializer.Deserialize<string>(setting.ValueJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The saved public URL is not valid JSON and is ignored");
            _current = null;
        }
    }
}
