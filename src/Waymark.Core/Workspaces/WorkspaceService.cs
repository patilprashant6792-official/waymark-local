using Microsoft.Extensions.Logging;
using Waymark.Core.Events;

namespace Waymark.Core.Workspaces;

public sealed class WorkspaceService(
    IWorkspaceStore store,
    IEventBus events,
    WorkspaceOptions options,
    TimeProvider clock,
    ILogger<WorkspaceService> logger) : IWorkspaceService
{
    public async Task<WorkspaceValidation> ValidateAsync(string? path, CancellationToken ct)
    {
        var (normalized, error) = WorkspacePathRules.NormalizePath(path);
        if (normalized is null)
        {
            return WorkspaceValidation.Invalid(error ?? "Folder path is not valid.");
        }

        if (!Directory.Exists(normalized))
        {
            return WorkspaceValidation.Invalid($"Folder does not exist: {normalized}");
        }

        try
        {
            var metadata = await WorkspaceScanner.ScanAsync(normalized, options, ct).ConfigureAwait(false);
            return metadata.SolutionFiles.Count == 0 && metadata.ProjectFiles.Count == 0
                ? WorkspaceValidation.Invalid("No solution (.sln/.slnx) or project (.csproj) files found.")
                : WorkspaceValidation.Valid(normalized, metadata);
        }
        catch (UnauthorizedAccessException)
        {
            return WorkspaceValidation.Invalid("Access to the folder was denied.");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Scanning workspace folder {Path} failed", normalized);
            return WorkspaceValidation.Invalid($"The folder could not be read: {ex.Message}");
        }
    }

    public async Task<WorkspaceAddResult> AddAsync(string? name, string? path, CancellationToken ct)
    {
        var nameError = WorkspacePathRules.ValidateName(name);
        if (nameError is not null)
        {
            return WorkspaceAddResult.Failure(nameError);
        }

        var validation = await ValidateAsync(path, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return WorkspaceAddResult.Failure(validation.Error ?? "Folder is not valid.");
        }

        var workspace = await store.AddAsync(name!, validation.NormalizedPath!, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (workspace is null)
        {
            return WorkspaceAddResult.Failure("A workspace with that name or folder already exists.");
        }

        logger.LogInformation("Workspace {Name} added ({Projects} projects)", workspace.Name, validation.Metadata!.ProjectFiles.Count);
        await events.PublishAsync(new WorkspaceChanged(workspace.Id, workspace.Name, WorkspaceChangeKind.Added)).ConfigureAwait(false);
        return WorkspaceAddResult.Success(workspace, validation.Metadata);
    }

    public Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken ct)
    {
        var existing = await FindAsync(id, ct).ConfigureAwait(false);
        if (existing is null || !await store.SetEnabledAsync(id, enabled, ct).ConfigureAwait(false))
        {
            return false;
        }

        logger.LogInformation("Workspace {Name} {State}", existing.Name, enabled ? "enabled" : "disabled");
        await events.PublishAsync(new WorkspaceChanged(id, existing.Name, enabled ? WorkspaceChangeKind.Enabled : WorkspaceChangeKind.Disabled)).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct)
    {
        var existing = await FindAsync(id, ct).ConfigureAwait(false);
        if (existing is null || !await store.RemoveAsync(id, ct).ConfigureAwait(false))
        {
            return false;
        }

        logger.LogInformation("Workspace {Name} removed", existing.Name);
        await events.PublishAsync(new WorkspaceChanged(id, existing.Name, WorkspaceChangeKind.Removed)).ConfigureAwait(false);
        return true;
    }

    private async Task<Workspace?> FindAsync(long id, CancellationToken ct) =>
        (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(w => w.Id == id);
}
