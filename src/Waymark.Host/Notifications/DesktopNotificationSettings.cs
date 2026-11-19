using System.Text.Json;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Settings;

namespace Waymark.Host.Notifications;

/// <summary>Whether approval notifications are on. Read before every notification, so a change on the Setup page applies at once.</summary>
public interface IDesktopNotificationSettings
{
    Task<bool> IsEnabledAsync(CancellationToken ct);
}

/// <summary>
/// The owner's switch for desktop notifications, stored as a global setting. On by default; a value that cannot be read counts as
/// on, because a notification that only says something waits does no harm. A change is written to the audit trail.
/// </summary>
public sealed class DesktopNotificationSettings(IConfigStore config, IAuditStore audit, TimeProvider time, ILogger<DesktopNotificationSettings> logger)
    : IDesktopNotificationSettings
{
    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.DesktopNotifications, ct).ConfigureAwait(false);
        if (setting is null)
        {
            return true;
        }

        try
        {
            return JsonSerializer.Deserialize<bool>(setting.ValueJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The desktop notification setting could not be read, so notifications stay on");
            return true;
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        var before = await IsEnabledAsync(ct).ConfigureAwait(false);
        await config.SetAsync(SettingScope.Global, SettingKeys.DesktopNotifications, enabled ? "true" : "false", AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        if (before != enabled)
        {
            await audit.AppendAsync(
                AuditEntries.Create(time, AuditCategory.Config, "config.desktop_notifications", AuthDefaults.AdminActor, SettingKeys.DesktopNotifications, args: enabled ? "on" : "off"),
                ct).ConfigureAwait(false);
        }
    }
}
