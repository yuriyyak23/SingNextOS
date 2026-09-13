using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed class ComputePlanner(RegionAuthority regions)
{
    public KernelResult<ComputeRegionCapabilityReceipt> QueryCapabilities(
        RegionOwner principal,
        ComputeRegionCapabilityQuery query,
        ComputeProviderCandidate provider)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(provider);
        if (query.ProviderId != provider.ProviderId || query.ProviderGeneration != provider.Generation)
            return KernelResult<ComputeRegionCapabilityReceipt>.Fail(KernelError.StaleGeneration, "Provider identity or generation is stale.");
        if (!provider.Available || provider.Faulted)
            return KernelResult<ComputeRegionCapabilityReceipt>.Fail(KernelError.PlatformUnavailable, "Provider is unavailable.");

        var readable = !query.RequestDeviceRead || regions.ProbeUse(query.Region.Region, principal, RegionUseMode.ReadOnly, query.Region.Range).IsSuccess;
        var writeMode = (provider.Capabilities & (ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess)) ==
                        (ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess)
            ? RegionUseMode.DirectCoherentWrite
            : RegionUseMode.StagedOutput;
        var writable = !query.RequestDeviceWrite || regions.ProbeUse(query.Region.Region, principal, writeMode, query.Region.Range).IsSuccess;
        var coherent = (provider.Capabilities & ComputeProviderCapabilities.CoherentHostAccess) != 0;
        var staged = true; // direct output is future-gated until CPU alias exclusion exists
        var security = !query.RequiresSecureEvidence
            ? SecureComputeAdmissionReadiness.NotRequired
            : (provider.Capabilities & ComputeProviderCapabilities.SecureComputeEvidence) != 0
                ? SecureComputeAdmissionReadiness.Ready
                : SecureComputeAdmissionReadiness.EvidenceUnavailable;
        return KernelResult<ComputeRegionCapabilityReceipt>.Ok(new(
            ComputePlanningContract.Version, provider.ProviderId, provider.Generation,
            readable, writable, coherent, staged,
            ExternalVisibilityRequirement.PublicationFence,
            security));
    }

    public KernelResult<ComputePlan> Plan(
        RegionOwner principal,
        ComputeIntent intent,
        ComputeSelectionPolicy policy,
        IReadOnlyList<ComputeProviderCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(candidates);
        var intentValidation = ValidateIntent(intent);
        if (!intentValidation.IsSuccess) return KernelResult<ComputePlan>.Fail(intentValidation.Error, intentValidation.Message!);

        var feasible = new List<(ComputeProviderCandidate Provider, ComputePublicationPath Path)>();
        foreach (var provider in candidates)
        {
            var path = SelectPath(intent, policy, provider);
            if (path is null || !ProviderMeetsIntent(provider, intent)) continue;
            var uses = RequiredUses(intent, path.Value);
            if (uses.Any(use => !regions.ProbeUse(use.Region, principal, use.Mode, use.Range).IsSuccess)) continue;
            feasible.Add((provider, path.Value));
        }

        if (feasible.Count == 0)
            return KernelResult<ComputePlan>.Fail(KernelError.PlatformUnavailable, "No available compute provider satisfies semantic policy, capability, security, virtualization and RegionUse requirements.");

        var selected = feasible
            .OrderBy(candidate => intent.PublicationPreference == ComputePublicationPreference.DirectPreferredWithStagedFallback && candidate.Path == ComputePublicationPath.DirectCoherent ? 0 : 1)
            .ThenBy(candidate => policy.PreferLowerLatency ? candidate.Provider.LatencyClass : 0)
            .ThenByDescending(candidate => policy.PreferHigherBandwidth ? candidate.Provider.BandwidthClass : 0)
            .ThenBy(candidate => candidate.Provider.ProviderId.Value, StringComparer.Ordinal)
            .First();
        var requiredUses = Array.AsReadOnly(RequiredUses(intent, selected.Path));
        var graph = BuildGraph(selected.Path);
        var graphValidation = ValidateGraph(graph);
        if (!graphValidation.IsSuccess) return KernelResult<ComputePlan>.Fail(graphValidation.Error, graphValidation.Message!);

        return KernelResult<ComputePlan>.Ok(new ComputePlan(
            intent,
            selected.Provider.ProviderId,
            selected.Provider.Generation,
            selected.Path,
            requiredUses,
            graph));
    }

    public KernelResult ValidateBeforeSubmit(
        ComputePlan plan,
        RegionOwner principal,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentCandidates);
        var provider = currentCandidates.SingleOrDefault(candidate => candidate.ProviderId == plan.ProviderId);
        if (provider is null || !provider.Available || provider.Faulted)
            return KernelResult.Fail(KernelError.PlatformUnavailable, "Planned compute provider disappeared or faulted; re-plan before submission.");
        if (provider.Generation != plan.ProviderGeneration)
            return KernelResult.Fail(KernelError.StaleGeneration, "Planned compute provider generation is stale; re-plan before submission.");
        if (!ProviderMeetsIntent(provider, plan.Intent) || !SupportsPath(provider, plan.PublicationPath))
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Planned provider no longer satisfies the exact semantic plan.");
        foreach (var use in plan.RequiredRegionUses)
        {
            var probe = regions.ProbeUse(use.Region, principal, use.Mode, use.Range);
            if (!probe.IsSuccess) return probe;
        }
        return ValidateGraph(plan.Dependencies);
    }

    public static KernelResult ValidateGraph(ComputeDependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Nodes is null || graph.Edges is null || graph.Nodes.Count == 0)
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute dependency graph requires nodes and edges.");
        if (graph.Nodes.Any(node => node.NodeId == 0 || !Enum.IsDefined(node.Kind)) || graph.Nodes.Select(node => node.NodeId).Distinct().Count() != graph.Nodes.Count)
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute dependency nodes require unique non-zero IDs and valid kinds.");
        var nodes = graph.Nodes.ToDictionary(node => node.NodeId);
        if (graph.Edges.Any(edge => edge.PredecessorNodeId == edge.SuccessorNodeId || !nodes.ContainsKey(edge.PredecessorNodeId) || !nodes.ContainsKey(edge.SuccessorNodeId)))
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute dependency edge is self-referential or names a missing node.");

        var successors = graph.Edges.GroupBy(edge => edge.PredecessorNodeId).ToDictionary(group => group.Key, group => group.Select(edge => edge.SuccessorNodeId).ToArray());
        var visiting = new HashSet<ulong>();
        var visited = new HashSet<ulong>();
        bool HasCycle(ulong node)
        {
            if (!visiting.Add(node)) return true;
            if (!visited.Contains(node) && successors.TryGetValue(node, out var next) && next.Any(HasCycle)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
        if (nodes.Keys.Any(node => !visited.Contains(node) && HasCycle(node)))
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute dependency graph contains a cycle.");

        var byKind = graph.Nodes.GroupBy(node => node.Kind).ToDictionary(group => group.Key, group => group.ToArray());
        var required = new[]
        {
            ComputeDependencyKind.InputPreparation,
            ComputeDependencyKind.MemoryPlacement,
            ComputeDependencyKind.DeviceSubmission,
            ComputeDependencyKind.DeviceCompletion,
            ComputeDependencyKind.VisibilityAcquire,
            ComputeDependencyKind.Publication,
            ComputeDependencyKind.DownstreamConsumerReady,
            ComputeDependencyKind.ReleaseReclaim
        };
        if (required.Any(kind => !byKind.TryGetValue(kind, out var matches) || matches.Length != 1))
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute dependency graph is missing a unique required lifecycle node.");
        var pathNodes = (byKind.GetValueOrDefault(ComputeDependencyKind.StagedOutputReady)?.Length ?? 0) +
                        (byKind.GetValueOrDefault(ComputeDependencyKind.DirectOutputBinding)?.Length ?? 0);
        if (pathNodes != 1)
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute dependency graph must describe exactly one staged or direct output path.");

        bool Reachable(ComputeDependencyKind from, ComputeDependencyKind to)
        {
            var start = byKind[from][0].NodeId;
            var target = byKind[to][0].NodeId;
            var pending = new Stack<ulong>();
            var seen = new HashSet<ulong>();
            pending.Push(start);
            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (current == target) return true;
                if (successors.TryGetValue(current, out var next))
                    foreach (var successor in next) pending.Push(successor);
            }
            return false;
        }

        var ordered = new[]
        {
            ComputeDependencyKind.InputPreparation,
            ComputeDependencyKind.MemoryPlacement,
            ComputeDependencyKind.DeviceSubmission,
            ComputeDependencyKind.DeviceCompletion,
            ComputeDependencyKind.VisibilityAcquire,
            ComputeDependencyKind.Publication,
            ComputeDependencyKind.DownstreamConsumerReady,
            ComputeDependencyKind.ReleaseReclaim
        };
        for (var index = 0; index < ordered.Length - 1; index++)
        {
            if (!Reachable(ordered[index], ordered[index + 1]))
                return KernelResult.Fail(KernelError.InvalidTransition, $"Compute dependency graph cannot consume {ordered[index + 1]} before {ordered[index]}.");
        }
        var pathKind = byKind.ContainsKey(ComputeDependencyKind.StagedOutputReady)
            ? ComputeDependencyKind.StagedOutputReady
            : ComputeDependencyKind.DirectOutputBinding;
        if (!Reachable(ComputeDependencyKind.MemoryPlacement, pathKind) ||
            !Reachable(pathKind, ComputeDependencyKind.DeviceSubmission))
            return KernelResult.Fail(KernelError.InvalidTransition, "Compute output-path node must connect memory placement to device submission.");
        return KernelResult.Ok();
    }

    private static KernelResult ValidateIntent(ComputeIntent intent)
    {
        if (!Enum.IsDefined(intent.Operation) || !Enum.IsDefined(intent.PublicationPreference))
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute intent operation or publication preference is invalid.");
        if (intent.Input.Region.RegionId.Value == 0 || intent.Output.Region.RegionId.Value == 0 || intent.Input.Region == intent.Output.Region)
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute intent requires distinct materialized input and output regions.");
        if (intent.Input.Range.Length <= 0 || intent.Output.Range.Length <= 0 || intent.Input.Range.Length != intent.Output.Range.Length)
            return KernelResult.Fail(KernelError.InvalidMessage, "Compute intent requires positive equal-length input and output ranges.");
        return KernelResult.Ok();
    }

    private static bool ProviderMeetsIntent(ComputeProviderCandidate provider, ComputeIntent intent) =>
        provider.Available &&
        !provider.Faulted &&
        provider.Generation != 0 &&
        !string.IsNullOrWhiteSpace(provider.ProviderId.Value) &&
        provider.MaximumOperationBytes >= intent.Input.Range.Length &&
        (provider.Capabilities & ComputeProviderCapabilities.AcceleratorExecution) != 0 &&
        (!intent.RequiresSecureEvidence || (provider.Capabilities & ComputeProviderCapabilities.SecureComputeEvidence) != 0) &&
        (!intent.RequiresVirtualizedDomain || (provider.Capabilities & ComputeProviderCapabilities.VirtualizedDomain) != 0);

    private static ComputePublicationPath? SelectPath(ComputeIntent intent, ComputeSelectionPolicy policy, ComputeProviderCandidate provider) =>
        intent.PublicationPreference switch
        {
            ComputePublicationPreference.StagedRequired when SupportsPath(provider, ComputePublicationPath.Staged) => ComputePublicationPath.Staged,
            ComputePublicationPreference.DirectRequired when SupportsPath(provider, ComputePublicationPath.DirectCoherent) => ComputePublicationPath.DirectCoherent,
            ComputePublicationPreference.DirectPreferredWithStagedFallback when SupportsPath(provider, ComputePublicationPath.DirectCoherent) => ComputePublicationPath.DirectCoherent,
            ComputePublicationPreference.DirectPreferredWithStagedFallback when policy.AllowStagedFallback && SupportsPath(provider, ComputePublicationPath.Staged) => ComputePublicationPath.Staged,
            _ => null
        };

    private static bool SupportsPath(ComputeProviderCandidate provider, ComputePublicationPath path) => path switch
    {
        ComputePublicationPath.Staged => (provider.Capabilities & ComputeProviderCapabilities.StagedPublication) != 0,
        ComputePublicationPath.DirectCoherent =>
            (provider.Capabilities & (ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess)) ==
            (ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess),
        _ => false
    };

    private static OperationRegionUseRequest[] RequiredUses(ComputeIntent intent, ComputePublicationPath path) =>
    [
        new OperationRegionUseRequest(intent.Input.Region, RegionUseMode.ReadOnly, intent.Input.Range),
        new OperationRegionUseRequest(
            intent.Output.Region,
            path == ComputePublicationPath.Staged ? RegionUseMode.StagedOutput : RegionUseMode.DirectCoherentWrite,
            intent.Output.Range)
    ];

    private static ComputeDependencyGraph BuildGraph(ComputePublicationPath path)
    {
        var pathKind = path == ComputePublicationPath.Staged
            ? ComputeDependencyKind.StagedOutputReady
            : ComputeDependencyKind.DirectOutputBinding;
        var kinds = new[]
        {
            ComputeDependencyKind.InputPreparation,
            ComputeDependencyKind.MemoryPlacement,
            pathKind,
            ComputeDependencyKind.DeviceSubmission,
            ComputeDependencyKind.DeviceCompletion,
            ComputeDependencyKind.VisibilityAcquire,
            ComputeDependencyKind.Publication,
            ComputeDependencyKind.DownstreamConsumerReady,
            ComputeDependencyKind.ReleaseReclaim
        };
        var nodes = kinds.Select((kind, index) => new ComputeDependencyNode((ulong)index + 1, kind)).ToArray();
        var edges = nodes.Zip(nodes.Skip(1), (left, right) => new ComputeDependencyEdge(left.NodeId, right.NodeId)).ToArray();
        return new ComputeDependencyGraph(Array.AsReadOnly(nodes), Array.AsReadOnly(edges));
    }
}
