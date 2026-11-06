namespace Waymark.Modules.Files.Replace;

/// <summary>Finds and replaces text across the files of one workspace in two steps: a preview that keeps a plan, and an apply of that plan.</summary>
public interface IReplaceService
{
    /// <summary>Reads and matches; writes nothing. The result carries a plan id only when the scan was complete and within the limits.</summary>
    /// <exception cref="ArgumentException">An argument is invalid, or <paramref name="request"/>.With holds a secret.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled or its folder is missing, or files cannot be listed.</exception>
    Task<ReplacePreview> PreviewAsync(ReplaceRequest request, CancellationToken ct);

    /// <summary>
    /// Writes the plan, minus the exclusions, if the arguments equal the preview's, the count equals <paramref name="confirmCount"/> and every
    /// file still has the bytes the preview read. All files change, or none; a refusal comes back as a result with Applied false.
    /// </summary>
    /// <exception cref="ArgumentException">An argument or an exclusion is invalid.</exception>
    /// <exception cref="KeyNotFoundException">The workspace does not exist.</exception>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    Task<ReplaceApplyResult> ApplyAsync(ReplaceRequest request, string planId, int confirmCount, IReadOnlyList<string> exclusions, CancellationToken ct);
}
