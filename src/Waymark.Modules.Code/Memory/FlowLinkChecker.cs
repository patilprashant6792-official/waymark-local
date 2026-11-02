using Waymark.Core.Code;
using Waymark.Core.Memory;
using Waymark.Modules.Code.Search;

namespace Waymark.Modules.Code.Memory;

/// <summary>What the code says about one link of a flow, and why when it is not a plain yes.</summary>
internal sealed record LinkCheck(FlowLinkState State, string? Detail);

/// <summary>
/// Checks each link of a flow (a step and the step it is called by) against the code. The calls come from the parsed file of the step above, so they are as fresh as the step itself;
/// the receiver of each call is sorted with the same rule code_callers uses, so both say the same about the same call.
/// <para>
/// Only a link between two steps that are in the code on this branch can fail. A step that is not here (another branch, a rename) makes its links <see cref="FlowLinkState.NotOnThisBranch"/>,
/// never a failure: a flow saved on another branch stays valid there.
/// </para>
/// </summary>
internal sealed class FlowLinkChecker(ICodeSearchStore store, CodeSearchOptions options)
{
    private const string ExtensionMarker = "(this ";
    private const int MaxGuessedNames = 400;

    /// <summary>One entry per step, in step order: the link from the step it is called by, or null for the first step and for steps with no parent.</summary>
    public async Task<IReadOnlyList<LinkCheck?>> CheckAsync(long workspaceId, IReadOnlyList<FlowStep> steps, IReadOnlyList<StepView> views, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(views);
        var result = new LinkCheck?[steps.Count];
        var hierarchy = new LazyHierarchy(store, options, workspaceId);
        for (var i = 1; i < steps.Count; i++)
        {
            if (FlowStepText.ParentIndex(steps, i) is not { } parent)
            {
                continue;
            }

            result[i] = await CheckOneAsync(workspaceId, steps[i], views[parent], views[i], hierarchy, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<LinkCheck> CheckOneAsync(long workspaceId, FlowStep step, StepView parent, StepView child, LazyHierarchy hierarchy, CancellationToken ct)
    {
        if (step.Link != FlowLinkKind.Call)
        {
            return new LinkCheck(FlowLinkState.Declared, FlowLinkKinds.ToText(step.Link));
        }

        if (!parent.Found || !child.Found)
        {
            return new LinkCheck(FlowLinkState.NotOnThisBranch, !parent.Found ? "the step above is not in the code here" : "this step is not in the code here");
        }

        if (parent.Member is null || parent.File?.Index is null)
        {
            return new LinkCheck(FlowLinkState.Declared, "the step above is a type, a file or a range of lines: it has no calls of its own to check");
        }

        if (child.Member is null)
        {
            return new LinkCheck(FlowLinkState.Declared, "a type, a file or a range of lines is not called by name");
        }

        if (child.Member.Kind == CodeMemberKind.Constructor)
        {
            return new LinkCheck(FlowLinkState.Declared, "a constructor is created, not called by name");
        }

        if (child.Member.Kind is not (CodeMemberKind.Method or CodeMemberKind.Function))
        {
            return new LinkCheck(FlowLinkState.Declared, "the index records calls of methods and functions, not reads of a property or field");
        }

        var calleeName = child.Member.Name;
        var calls = OutgoingCalls(parent).Where(call => string.Equals(call.CalleeName, calleeName, StringComparison.Ordinal)).ToList();
        if (calls.Count == 0)
        {
            return new LinkCheck(FlowLinkState.DoesNotCall, $"{parent.Member.Name} makes no call to {calleeName}");
        }

        if (child.Owner is null)
        {
            // A module-level function: a call with no receiver reaches it.
            return calls.Any(call => call.Receiver is null)
                ? new LinkCheck(FlowLinkState.Calls, null)
                : new LinkCheck(FlowLinkState.NameOnly, "a call of that name is there, on a receiver");
        }

        if (child.Member.Signature.Contains(ExtensionMarker, StringComparison.Ordinal))
        {
            return new LinkCheck(FlowLinkState.NameOnly, "extension methods are matched by name only");
        }

        var (names, problem) = await hierarchy.RelatedAsync(child.Owner.Name, ct).ConfigureAwait(false);
        if (names is null)
        {
            return new LinkCheck(FlowLinkState.NameOnly, problem);
        }

        var guessed = calls
            .Where(call => call.ReceiverTypeGuessed && call.ReceiverType is not null)
            .Select(call => call.ReceiverType!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxGuessedNames)
            .ToList();
        var valueNames = guessed.Count == 0
            ? new HashSet<string>()
            : await store.FindDeclaredValueNamesAsync(workspaceId, guessed, ct).ConfigureAwait(false);
        var matches = calls.Select(call => ReceiverFilter.MatchReceiver(call.ReceiverType, call.ReceiverTypeGuessed, names, valueNames)).ToList();
        if (matches.Contains(SiteMatch.Typed))
        {
            return new LinkCheck(FlowLinkState.Calls, null);
        }

        if (matches.Contains(SiteMatch.NameOnly))
        {
            return new LinkCheck(FlowLinkState.NameOnly, "a call of that name is there, but the type of its receiver is unknown");
        }

        var types = calls.Select(call => call.ReceiverType).Where(type => type is not null).Distinct(StringComparer.Ordinal).Take(4);
        return new LinkCheck(FlowLinkState.DoesNotCall, $"{parent.Member.Name} calls {calleeName} only on {string.Join(", ", types)}, not on {child.Owner.Name}");
    }

    // The index stores a call's caller as type and member name, so the calls a step makes are the sites in its own file with that pair.
    private static List<CodeCallSiteEntry> OutgoingCalls(StepView parent)
    {
        var owner = parent.Owner;
        var name = parent.Member!.Name;
        return parent.File!.Index!.CallSites
            .Where(site => string.Equals(site.CallerMember, name, StringComparison.Ordinal)
                && (owner is null
                    ? site.CallerType is null
                    : string.Equals(site.CallerType, owner.Name, StringComparison.Ordinal)
                        || string.Equals(site.CallerType, owner.FullName, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>The type hierarchy of the workspace, read once and only when a link needs it.</summary>
    private sealed class LazyHierarchy(ICodeSearchStore store, CodeSearchOptions options, long workspaceId)
    {
        private TypeHierarchy? _hierarchy;
        private string? _problem;
        private bool _loaded;

        public async Task<(IReadOnlySet<string>? Names, string? Problem)> RelatedAsync(string typeName, CancellationToken ct)
        {
            if (!_loaded)
            {
                _loaded = true;
                var relations = await store.FindTypeRelationsAsync(workspaceId, options.MaxTypeRelations, ct).ConfigureAwait(false);
                if (relations.Capped)
                {
                    _problem = "the type hierarchy is too large to read completely";
                }
                else
                {
                    _hierarchy = new TypeHierarchy(relations.Rows);
                }
            }

            if (_hierarchy is null)
            {
                return (null, _problem);
            }

            var related = _hierarchy.RelatedNames(typeName);
            return related is null
                ? (null, "the type hierarchy around this type is too deep or wide to read completely")
                : (related, null);
        }
    }
}
