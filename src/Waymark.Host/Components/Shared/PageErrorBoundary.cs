using Microsoft.AspNetCore.Components.Web;
using Waymark.Core.LiveFeed;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// Keeps a page that throws while it draws from taking the whole circuit down: the layout stays, the page is replaced by a short notice,
/// and the failure is written to the Live log. The default boundary logs through the framework; this one reports like every other failure.
/// </summary>
public sealed class PageErrorBoundary : ErrorBoundary
{
    protected override Task OnErrorAsync(Exception exception)
    {
        FailureReporter.Report(exception, "Drawing a page");
        return Task.CompletedTask;
    }
}
